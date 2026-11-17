// Round 8 B6 (docs/207 §9.1): offline fixtures for the never-yielding Sims3Gameplay* accessors of docs/207 §9. Each method
// builds the objects an accessor reads (real constructors, private fields set by reflection; this mscorlib has no
// GetUninitializedObject) and calls it once for input i. tools/jit-corpus.ts wraps every call in a corpus case, so the
// value or the exception is hashed interpreted twice and compiled once (MONOHOST_JITCORPUS=1, docs/207 §10). C# 2.
// Compiled against the game's Sims3GameplaySystems.dll and ScriptCore.dll (build.cmd). Hand-written, not generated.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.SimIFace.Enums;

namespace MonoHostTest
{
    // a concrete GameObject: GameObject's constructor only sets a delegate, ScriptObject's does nothing
    public class FxObject : GameObject { }
    public class FxLot : Lot { }
    public class FxCompA : ObjectComponent { public override Type BaseType { get { return typeof(FxCompA); } } }
    public class FxCompB : ObjectComponent { public override Type BaseType { get { return typeof(FxCompB); } } }
    public class FxCompC : ObjectComponent { public override Type BaseType { get { return typeof(FxCompC); } } }
    public class FxRouting : RoutingComponent { public override Route CreateRoute() { return null; } }
    public class FxPortal : PortalComponent
    {
        public override PortalObjectType GetPortalObjectType() { return default(PortalObjectType); }
        protected override bool AddToUseListDuringTraversal { get { return false; } }
        protected override bool RemoveFromUseListAfterTraversal { get { return false; } }
        public override uint GetNumLanes() { return 0; }
        public override Slot[] GetSlotsForLaneIndex(uint lane) { return null; }
        public override List<Slot> GetStartSlotsForApproachingSim(Sim sim, Vector3 pos, ref bool b) { return null; }
        public override bool OnPortalStart(Sim sim) { return false; }
        public override bool CanUseUmbrella { get { return false; } }
    }
    public class FxPart : PartComponent { }

    // a proxy that answers only its ObjectId; the host never calls the rest
    public class FxProxy : IScriptProxy
    {
        ObjectGuid mId;
        public FxProxy(ulong id) { mId = new ObjectGuid(id); }
        public ObjectGuid ObjectId { get { return mId; } set { mId = value; } }
        public IScriptLogic Target { get { return null; } }
        public string ClassName { get { return "FxProxy"; } }
        public ScriptExecuteType ExecuteType { get { return ScriptExecuteType.None; } }
        public void Simulate() { }
        public void Stop() { }
        public void Dispose() { }
        public bool CreateLogic(string name) { return false; }
        public ResourceKey GetResourceKey() { return ResourceKey.kInvalidResourceKey; }
        public void PostLoad() { }
        public void OnReset() { }
        public void SetObjectToReset() { }
        public bool OnScriptError(Exception e) { return false; }
    }

    public static class JitAcc
    {
        const BindingFlags kAll = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        static FieldInfo F(Type t, string name)
        {
            for (Type x = t; x != null; x = x.BaseType) { FieldInfo f = x.GetField(name, kAll); if (f != null) return f; }
            throw new MissingFieldException(t.FullName, name);
        }
        static void Set(object o, string name, object v) { F(o.GetType(), name).SetValue(o, v); }
        static object Get(object o, string name) { return F(o.GetType(), name).GetValue(o); }
        static void SetS(Type t, string name, object v) { F(t, name).SetValue(null, v); }
        static object GetS(Type t, string name) { return F(t, name).GetValue(null); }
        static long Bits(float a, float b) { return ((long)BitConverter.ToInt32(BitConverter.GetBytes(a), 0) << 32) | (uint)BitConverter.ToInt32(BitConverter.GetBytes(b), 0); }

        // 1. GameObject.GetComponent<C> (private: called through reflection; the compiled entry is the method's own, so the
        //    caller does not matter): the component list holds A and B
        static readonly MethodInfo s_getComponent = typeof(GameObject).GetMethod("GetComponent", BindingFlags.Instance | BindingFlags.NonPublic);
        public static object GetComponentCase(int i)
        {
            FxObject o = new FxObject();
            List<ObjectComponent> list = new List<ObjectComponent>();
            list.Add(new FxCompA()); list.Add(new FxCompB());
            if (i != 3) Set(o, "mObjComponents", list);
            Type want = i == 1 ? typeof(FxCompB) : i == 2 ? typeof(FxCompC) : typeof(FxCompA);
            try { return s_getComponent.MakeGenericMethod(want).Invoke(o, null); }
            catch (TargetInvocationException e) { throw e.InnerException; }
        }

        // 2. InteractionObjectPair.Equals: two of Sim's own definitions (their Singleton fields), two targets
        static InteractionDefinition[] s_defs;
        static InteractionDefinition Def(int k)
        {
            if (s_defs == null)
            {
                List<InteractionDefinition> l = new List<InteractionDefinition>();
                foreach (Type t in typeof(Sim).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    FieldInfo f = t.GetField("Singleton", BindingFlags.Static | BindingFlags.Public);
                    if (f == null || !typeof(InteractionDefinition).IsAssignableFrom(f.FieldType)) continue;
                    InteractionDefinition d = f.GetValue(null) as InteractionDefinition;
                    if (d != null) l.Add(d);
                    if (l.Count == 2) break;
                }
                s_defs = l.ToArray();
            }
            return s_defs[k % s_defs.Length];
        }
        public static bool IopEqualsCase(int i)
        {
            FxObject a = new FxObject(), b = new FxObject();
            InteractionObjectPair p = new InteractionObjectPair(Def(0), a);
            switch (i)
            {
                case 0: return p.Equals(new InteractionObjectPair(Def(0), a));
                case 1: return p.Equals(new InteractionObjectPair(Def(0), b));
                case 2: return p.Equals(new InteractionObjectPair(Def(1), a));
                case 3: return p.Equals("x");
                case 4: return p.Equals(null);
                case 5: return p.Equals(p);
                default: return new InteractionObjectPair(null, a).Equals(p);   // null definition: the NullReference path
            }
        }

        // 3. GameObject.GetObject: the invalid guid returns null without the simulator; the others ask Simulator.GetProxy
        public static object GetObjectCase(int i)
        {
            switch (i)
            {
                case 0: return GameObject.GetObject(ObjectGuid.InvalidObjectGuid);
                case 1: return GameObject.GetObject(new ObjectGuid(0x1234UL));
                default: return GameObject.GetObject(new ObjectGuid(ulong.MaxValue));
            }
        }

        // jit-piece-bench cases 41-44 (Wide=1 target shapes, docs/537 §10): the game's SimClock.ElapsedTimeInMinutes (ldind.r4, sub.r8, stind.r4),
        // DesireCurve.GetYFromX (sub / mul / add .r8), GameObject.GetComponent<C> (ldtoken, castclass) built once (get_LotCurrent needs the Slots icall, a host stub: no case)
        public delegate object BenchGet();
        public static BenchGet BenchGc;
        public static DesireCurve BenchCurve;
        static bool s_benchWide;
        public static void BenchWideSetup()
        {
            if (s_benchWide) return;
            s_benchWide = true;
            Type c = typeof(SimClock);
            SetS(c, "sSimTickCnt", 54000L);
            SetS(c, "sStartTime", new DateAndTime(1800L));
            SetS(c, "sTicksAdvanced", 7919L);
            FxObject o = new FxObject();
            List<ObjectComponent> list = new List<ObjectComponent>();
            list.Add(new FxCompA()); list.Add(new FxCompB());
            Set(o, "mObjComponents", list);
            BenchGc = (BenchGet)Delegate.CreateDelegate(typeof(BenchGet), o, s_getComponent.MakeGenericMethod(typeof(FxCompB)));
            BenchCurve = new DesireCurve(new Vector2[] { new Vector2(0f, 0f), new Vector2(1f, 10f), new Vector2(1f, 20f), new Vector2(3f, 0f) });
        }

        // 4. SimClock.ElapsedTimeInMinutes(ref float): three statics set, the result and the written lastTime
        static readonly long[] kTicks = { 0, 1, 1800, 54000, 1234567, long.MaxValue / 4, -1 };
        static readonly float[] kLast = { 0f, 1.5f, -3f, 1e9f, float.NaN };
        public static long ElapsedMinutesCase(int i)
        {
            Type c = typeof(SimClock);
            object t0 = GetS(c, "sSimTickCnt"), s0 = GetS(c, "sStartTime"), a0 = GetS(c, "sTicksAdvanced");
            try
            {
                SetS(c, "sSimTickCnt", kTicks[i % kTicks.Length]);
                SetS(c, "sStartTime", new DateAndTime(kTicks[(i / kTicks.Length) % kTicks.Length]));
                SetS(c, "sTicksAdvanced", (long)(i % 3) * 7919);
                float last = kLast[i % kLast.Length];
                float r = SimClock.ElapsedTimeInMinutes(ref last);
                return Bits(r, last);
            }
            finally { SetS(c, "sSimTickCnt", t0); SetS(c, "sStartTime", s0); SetS(c, "sTicksAdvanced", a0); }
        }

        // 5. GameObject.get_Parent: Slots.GetParentId(ObjectId) then GetObject; the proxy decides the id
        public static object ParentCase(int i)
        {
            FxObject o = new FxObject();
            if (i == 1) o.Proxy = new FxProxy(5);
            return o.Parent;
        }

        // 6. ScriptObject.get_ObjectId: no proxy -> InvalidObjectGuid; a proxy -> its id
        public static ulong ObjectIdCase(int i)
        {
            FxObject o = new FxObject();
            if (i == 1) o.Proxy = new FxProxy(77);
            return o.ObjectId.Value;
        }

        // 7. GameObject.get_LotCurrent: walks Parent (none here) and reads mLotCurrent
        public static object LotCurrentCase(int i)
        {
            FxObject o = new FxObject();
            if (i == 1) Set(o, "mLotCurrent", new FxLot());
            return o.LotCurrent;
        }

        // 8. Household.Members.GetActorsOfType: two human descriptions, one pet; only case 2 gives one of them a Sim object
        static SimDescription Desc(CASAgeGenderFlags flags)
        {
            SimDescription d = new SimDescription();
            Set(d, "mSimFlags", flags);
            return d;
        }
        public static int ActorsOfTypeCase(int i)
        {
            Household.Members m = new Household.Members();
            List<SimDescription> humans = (List<SimDescription>)Get(m, "mSimDescriptions"), pets = (List<SimDescription>)Get(m, "mPetSimDescriptions");
            SimDescription adult = Desc(CASAgeGenderFlags.Adult | CASAgeGenderFlags.Human | CASAgeGenderFlags.Male);
            humans.Add(adult);
            humans.Add(Desc(CASAgeGenderFlags.Teen | CASAgeGenderFlags.Human | CASAgeGenderFlags.Female));
            pets.Add(Desc(CASAgeGenderFlags.Adult | CASAgeGenderFlags.Dog | CASAgeGenderFlags.Male));
            if (i == 2) Set(adult, "mSim", new Sim());
            CASAGSAvailabilityFlags type = i == 1 ? CASAGSAvailabilityFlags.AllAnimalsMask : i == 3 ? CASAGSAvailabilityFlags.None
                : i == 4 ? CASAGSAvailabilityFlags.HumanTeen : CASAGSAvailabilityFlags.HumanAgeMask;
            return m.GetActorsOfType(type).Count;
        }

        // 9. RoutingComponent.get_IsRouting reads the route list's size
        public static bool IsRoutingCase(int i)
        {
            RoutingComponent rc = new FxRouting();
            object list = Get(rc, "mRouteActions");
            F(list.GetType(), "_size").SetValue(list, i);   // 0, 1, 3: the accessor reads nothing else of the list
            return rc.IsRouting;
        }

        // 10. Lot.get_CommercialLotSubType maps the stored sub type
        static readonly int[] kSub = { 0, 1, 5, 9, 100, -1 };
        public static int LotSubTypeCase(int i)
        {
            FxLot lot = new FxLot();
            Set(lot, "mCommercialSubType", (CommercialLotSubType)kSub[i % kSub.Length]);
            return (int)lot.CommercialLotSubType;
        }

        // 11. ListSorted<int>.GetEnumerator after sorted inserts
        public static int ListSortedCase(int i)
        {
            ListSorted<int> ls = new ListSorted<int>();
            int[][] sets = { new int[0], new int[] { 5, 1, 3 }, new int[] { 2, 2, 2 }, new int[] { int.MinValue, 0, int.MaxValue, -1 } };
            foreach (int v in sets[i % sets.Length]) ls.Add(v);
            int h = 1;
            foreach (int v in ls) h = unchecked(h * 31 + v);
            return h;
        }

        // 12. DesireCurve.GetYFromX: a four-point curve with a vertical step, and the empty curve (its own catch returns 0)
        static readonly float[] kX = { -1f, 0f, 0.5f, 1f, 1.5f, 2f, 3f, 4f, float.NaN, float.PositiveInfinity, float.NegativeInfinity };
        public static float DesireCurveCase(int i)
        {
            Vector2[] pts = i >= kX.Length ? new Vector2[0]
                : new Vector2[] { new Vector2(0f, 0f), new Vector2(1f, 10f), new Vector2(1f, 20f), new Vector2(3f, 0f) };
            return new DesireCurve(pts).GetYFromX(kX[i % kX.Length]);
        }

        // 13. Sim.get_SimDescription: the field, set or null
        public static object SimDescriptionCase(int i)
        {
            Sim s = new Sim();
            if (i == 1) Set(s, "mSimDescription", Desc(CASAgeGenderFlags.Elder | CASAgeGenderFlags.Human));
            return s.SimDescription;
        }

        // 14. Lot.get_ShouldSimulate: the active lot, the world lot flag and the lot type decide
        public static bool ShouldSimulateCase(int i)
        {
            FxLot lot = new FxLot();
            object active = GetS(typeof(LotManager), "sActiveLot");
            try
            {
                SetS(typeof(LotManager), "sActiveLot", (i & 1) != 0 ? lot : null);
                Set(lot, "mLotType", (i & 2) != 0 ? LotType.Commercial : LotType.Residential);
                return lot.ShouldSimulate;
            }
            finally { SetS(typeof(LotManager), "sActiveLot", active); }
        }

        // 15. SimDescription.get_Age masks the flags
        static readonly CASAgeGenderFlags[] kAge = { CASAgeGenderFlags.Adult, CASAgeGenderFlags.Teen | CASAgeGenderFlags.Male,
            CASAgeGenderFlags.Child | CASAgeGenderFlags.Female | CASAgeGenderFlags.Human, CASAgeGenderFlags.None, unchecked((CASAgeGenderFlags)(-1)) };
        public static int AgeCase(int i) { return (int)Desc(kAge[i % kAge.Length]).Age; }

        // 16. Corners.get_Item: four corners of a rectangle, then one past the end
        public static float CornersCase(int i)
        {
            Corners c = new Corners(new Sims3.SimIFace.Rect(1f, 2f, 10f, 20f));
            Vector3 v = c[i];
            return v.x * 1000f + v.y * 10f + v.z;
        }

        // 17. Household.EveryHumanSimDescription over the static household list: empty, then null
        public static int EveryHumanCase(int i)
        {
            List<Household> saved = Household.sHouseholdList;
            try
            {
                Household.sHouseholdList = i == 0 ? new List<Household>() : null;
                return Household.EveryHumanSimDescription().Count;
            }
            finally { Household.sHouseholdList = saved; }
        }

        // 18. ScriptObject.get_Proxy: no proxy (null), then a proxy; ScriptProxy.get_ObjectId on a real ScriptProxy
        public static ulong ProxyCase(int i)
        {
            FxObject o = new FxObject();
            if (i == 1) o.Proxy = new FxProxy(9);
            return o.Proxy == null ? 0UL : o.Proxy.ObjectId.Value + 1UL;
        }

        public static ulong ProxyIdCase(int i)
        {
            ScriptCore.ScriptProxy p = (ScriptCore.ScriptProxy)Activator.CreateInstance(typeof(ScriptCore.ScriptProxy), true);
            if (i == 1) p.ObjectId = new ObjectGuid(0x1234UL);
            return p.ObjectId.Value;
        }

        // 19. the BaseType override of every listed ObjectComponent subclass (each returns typeof(itself)); the instance comes from
        // its parameterless constructor, the getter reads no field
        static readonly string[] kBaseTypeNames = {
            "ItemComponent", "PortalComponent", "DonatableComponent", "ObjectOwnerComponent",
            "RoutingComponent", "MaintenanceComponent", "InteractionsDisabledComponent", "BoobyTrapComponent",
            "CleanableComponent", "PartComponent", "PetSurfaceComponent", "LootComponent",
            "DoorRouteThroughComponent", "RepairableComponent", "CatHuntingComponent", "TombObjectStaticComponent",
            "TombObjectDynamicComponent", "SandsOfUnderstandingComponent", "ResortStaffedObjectComponent", "SittableComponent",
            "HarvestableComponent", "PlantableComponent", "UpgradableComponent", "AdventureComponent",
            "NameComponent", "AdventureFXComponent", "ScienceComponent", "ComeAndSeeComponent",
            "SyphonableComponent", "InventoryComponent", "FertilizerComponent", "StairsRouteThroughComponent",
            "OpportunitySetupObjectComponent", "SculptureComponent", "OverlayComponent", "TreasureComponent",
            "WorldBuilderDeathComponent" };
        static Type[] sCompTypes;
        public static string BaseTypeCase(int i)
        {
            if (sCompTypes == null) sCompTypes = typeof(ObjectComponent).Assembly.GetTypes();
            string name = kBaseTypeNames[i];
            Type found = null;
            foreach (Type t in sCompTypes)
                if (t.Name == name && typeof(ObjectComponent).IsAssignableFrom(t)) { found = t; break; }
            if (found == null) throw new MissingMemberException(name);
            // an exception in a fixture would end the whole case (EA's interpreter carries it past the loop's catch): abstract ones get a bare subclass
            ObjectComponent c = found == typeof(RoutingComponent) ? new FxRouting() : found == typeof(PortalComponent) ? (ObjectComponent)new FxPortal()
                : found == typeof(PartComponent) ? (ObjectComponent)new FxPart() : (ObjectComponent)Activator.CreateInstance(found, true);
            return c.BaseType.Name;
        }

        // 20. the Enumerator constructors no foreach reaches (the BCL builds those through GetEnumerator with a mode):
        // Hashtable, SortedList, SortedList<,>, MatchCollection, and LinkedList<T>'s serialization constructor
        const BindingFlags kCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static object NewNested(Type outer, string nested, params object[] args)
        {
            Type t = outer.GetNestedType(nested, BindingFlags.Public | BindingFlags.NonPublic);
            return Activator.CreateInstance(t, kCtor, null, args, null);
        }
        public static int EnumCtorCase(int i)
        {
            switch (i)
            {
                case 0: { Hashtable h = new Hashtable(); h.Add(1, 2); h.Add(3, 4); return Drain((IEnumerator)NewNested(typeof(Hashtable), "Enumerator", h)); }
                case 1: { SortedList s = new SortedList(); s.Add(1, 2); s.Add(3, 4); return Drain((IEnumerator)NewNested(typeof(SortedList), "Enumerator", s)); }
                case 2:
                {
                    SortedList<int, int> s = new SortedList<int, int>(); s.Add(1, 2); s.Add(3, 4);
                    Type t = typeof(SortedList<,>).GetNestedType("Enumerator", BindingFlags.Public | BindingFlags.NonPublic).MakeGenericType(typeof(int), typeof(int));
                    return Drain((IEnumerator)Activator.CreateInstance(t, kCtor, null, new object[] { s }, null));
                }
                case 3: { MatchCollection m = Regex.Matches("a1b22c", "[0-9]+"); return Drain((IEnumerator)NewNested(typeof(MatchCollection), "Enumerator", m)); }
                default:
                {
                    LinkedList<int> l = new LinkedList<int>(); l.AddLast(5); l.AddLast(6);
                    SerializationInfo si = new SerializationInfo(typeof(LinkedList<int>), new FormatterConverter());
                    si.AddValue("owner", l, typeof(LinkedList<int>)); si.AddValue("version", 1U); si.AddValue("index", 0);
                    Type t = typeof(LinkedList<>).GetNestedType("Enumerator", BindingFlags.Public | BindingFlags.NonPublic).MakeGenericType(typeof(int));
                    object e = Activator.CreateInstance(t, kCtor, null, new object[] { si, new StreamingContext() }, null);
                    return e.GetType() == t ? 1 : 0;
                }
            }
        }
        static int Drain(IEnumerator e)
        {
            int n = 0;
            while (e.MoveNext()) n++;
            return n;
        }

        // 22. M2 (docs/537 §31): Motive.UpdateMotiveWithDecay, a class-Y method with two finally clauses (the List<T>.Enumerator
        // foreach loops), admitted by hand ([MonoJit] YRows=1). Input i: commodity i % 9; r = i / 9: bit 0 buffs present,
        // bits 1-2 the interaction (0 none, 1 three deltas, 2 a matching delta then a null one: NullReferenceException in this
        // frame inside the try, 3 the MotiveDeltas override throws), bit 3 a fixture posture (unlisted virtual target), bit 4 no
        // TraitManager (a callee throws inside the try when Fun / BatteryPower gain), bit 5 a negative value.
        static readonly CommodityKind[] kMotive = { CommodityKind.Fun, CommodityKind.Hygiene, CommodityKind.Energy, CommodityKind.Social,
            CommodityKind.Hunger, CommodityKind.Temperature, CommodityKind.VampireThirst, CommodityKind.AuraPower, CommodityKind.BatteryPower };
        public const int MotiveInputs = 9 * 64;
        // an exception as a value: its type and the throwing frame's position ("#0: 0x0095c callvirt in ..." -> "#0: 0x0095c"), in a
        // finite float no decay result can take (at most -1024; results are clamped to the tuning's -100..100)
        static float Thrown(Exception e)
        {
            string t = e.StackTrace ?? "";
            int nl = t.IndexOf('\n');
            string f = (nl < 0 ? t : t.Substring(0, nl)).Trim();
            int sp = f.Length > 4 ? f.IndexOf(' ', 4) : -1;
            int h = unchecked(e.GetType().FullName.GetHashCode() * 31 + (sp > 0 ? f.Substring(0, sp) : f).GetHashCode());
            s_cov[4 + s_dm]++;
            s_cov[f.IndexOf("Motive:UpdateMotiveWithDecay") >= 0 ? 8 : 9]++;
            return -1024f * (1 + (h & 0xFFFFF));  // exact: 20 bits times a power of two
        }
        // coverage for the host log (JitMotiveStat): [0-3] inputs that returned a value by interaction shape (bits 1-2 of r), [4-7] that
        // threw, [8] threw in the method's own frame, [9] in a callee, [10] fixture posture calls, [11] MotiveDeltas getter calls.
        // Shape 2 (a null delta after a matching one) throws at the loop body's MotiveDelta.Motive read: inside the first foreach's try,
        // so its unwind runs that finally (the enumerator's Dispose). The trace offsets are mint positions, not IL offsets.
        static readonly int[] s_cov = new int[12];
        static int s_dm;
        public static void MotiveCov(int k) { s_cov[k]++; }
        public static int MotiveStat(int k)  // reads s_cov[k] and clears it
        {
            int v = s_cov[k];
            s_cov[k] = 0;
            return v;
        }
        static readonly MethodInfo s_decay = typeof(Motive).GetMethod("UpdateMotiveWithDecay", BindingFlags.Instance | BindingFlags.NonPublic);
        static MotiveTuning Tuning(CommodityKind k, int decayType)
        {
            foreach (ConstructorInfo c in typeof(MotiveTuning).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                ParameterInfo[] p = c.GetParameters();
                if (p.Length != 20) continue;
                object[] a = new object[20];
                for (int j = 0; j < 20; j++) a[j] = p[j].ParameterType.IsValueType ? Activator.CreateInstance(p[j].ParameterType) : null;
                a[0] = k;
                a[8] = new DesireCurve(new Vector2[] { new Vector2(-100f, 0f), new Vector2(100f, 1f) });
                a[9] = Enum.ToObject(p[9].ParameterType, decayType);
                a[10] = 2.5f;
                return (MotiveTuning)c.Invoke(a);
            }
            throw new MissingMethodException("MotiveTuning", ".ctor(20)");
        }
        static void Add(TraitManager tm, TraitNames t) { ((Dictionary<ulong, Trait>)Get(tm, "mValues")).Add((ulong)t, null); }
        static void Add(BuffManager bm, BuffNames b) { ((Dictionary<ulong, BuffInstance>)Get(bm, "mValues")).Add((ulong)b, null); }
        public static Sim MotiveSim(int r, InteractionInstance inter, Posture posture)
        {
            Sim s = new Sim();
            SimDescription d = Desc(CASAgeGenderFlags.Adult | CASAgeGenderFlags.Human);
            Set(s, "mSimDescription", d);
            if ((r & 16) == 0)
            {
                TraitManager tm = new TraitManager();
                if ((r & 1) != 0) { Add(tm, TraitNames.Carefree); Add(tm, TraitNames.SocialButterfly); Add(tm, TraitNames.EfficientChip); }
                d.TraitManager = tm;
            }
            BuffManager bm = new BuffManager(s);
            if ((r & 1) != 0)
                foreach (BuffNames b in new BuffNames[] { BuffNames.TooManyPeople, BuffNames.GoingWild, BuffNames.PestilencePlague, BuffNames.TooMuchSun,
                    BuffNames.LaidbackLemon, BuffNames.FairyAuraProjectingSoothe, BuffNames.AbsorbingSunlight, BuffNames.FrostyFace })
                    Add(bm, b);
            Set(s, "mBuffManager", bm);
            if (inter != null)
            {
                InteractionQueue q = new InteractionQueue(s);
                ((List<InteractionInstance>)Get(q, "mInteractionList")).Add(inter);
                Set(q, "mIsHeadInteractionActive", true);
                Set(s, "mInteractionQueue", q);
            }
            if (posture != null) Set(s, "mPosture", posture);
            return s;
        }
        public static float MotiveDecayCase(int i) { return MotiveDecay(i, true); }
        delegate float MotiveDecayFn(float hoursPassed, float currentHours24, Sim sim);
        static float MotiveDecay(int i, bool direct)
        {
            CommodityKind k = kMotive[i % kMotive.Length];
            int r = i / kMotive.Length, dm = (r >> 1) & 3;
            s_dm = dm;
            FxMotiveInteraction inter = null;
            if (dm != 0)
            {
                inter = new FxMotiveInteraction();
                inter.Throws = dm == 3;
                inter.Deltas = new List<MotiveDelta>();
                if (dm == 1)
                {
                    inter.Deltas.Add(new MotiveDelta(CommodityKind.Bladder, 9f, true, true));
                    inter.Deltas.Add(new MotiveDelta(k, 3f, false, true));
                    MotiveDelta below = new MotiveDelta(k, -4f, true, false);
                    below.Multiplier = 0.5f;
                    inter.Deltas.Add(below);
                }
                else { inter.Deltas.Add(new MotiveDelta(k, 1.5f, true, true)); inter.Deltas.Add(null); }
            }
            Motive m = new Motive();
            m.Commodity = k;
            m.Tuning = Tuning(k, i % 3);
            Set(m, "mValue", (r & 32) != 0 ? -37.5f : 42.25f);
            Sim s = MotiveSim(r, inter, (r & 8) != 0 ? new FxMotivePosture() : null);
            // the exception is hashed here, not rethrown: EA's interpreter carries a throw out of a catch handler past the corpus
            // case's own catch (docs/537 §31), which ended the case at its first throwing input
            // direct (the yield task): a delegate call, since a task cannot yield across runtime_invoke
            if (direct)
            {
                try { float v = ((MotiveDecayFn)Delegate.CreateDelegate(typeof(MotiveDecayFn), m, s_decay))(0.25f, 13.5f, s); s_cov[dm]++; return v; }
                catch (Exception e) { return Thrown(e); }
            }
            try { return (float)s_decay.Invoke(m, new object[] { 0.25f, 13.5f, s }); }
            catch (TargetInvocationException e) { return Thrown(e.InnerException); }
        }
        // the yield test of monohost-jit.cpp (JitMotiveYieldTask): run as a task, the fixture posture sleeps one tick inside the
        // virtual call, so the compiled caller's frame is suspended in EA's interpreter and resumed past the callvirt exit
        public static int MotiveYieldTask(int n)
        {
            int h = 17;
            FxMotivePosture.Yield = true;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    float v = MotiveDecay(((i & 1) != 0 ? 9 * 9 : 8 * 9) + i % 9, true);  // r = 8 or 9 (posture, buffs), commodity i % 9
                    h = unchecked(h * 31 + BitConverter.ToInt32(BitConverter.GetBytes(v), 0));
                }
            }
            finally { FxMotivePosture.Yield = false; }
            return h;
        }
    }

    // M2 fixtures: an interaction whose MotiveDeltas override is a virtual target the compiled body cannot enter (not listed),
    // and a posture standing in for the two yielding store-object postures (BabySwing.InSwingPosture, WalkerClassic.InWalkerPosture)
    public class FxMotiveInteraction : InteractionInstance
    {
        public List<MotiveDelta> Deltas;
        public bool Throws;
        public override List<MotiveDelta> MotiveDeltas
        {
            get { JitAcc.MotiveCov(11); if (Throws) throw new InvalidOperationException("fx deltas"); return Deltas; }
        }
    }
    public class FxMotivePosture : Posture
    {
        public static bool Yield;
        public override float GetPostureMotiveDecayUpdate(float currentValue, CommodityKind commodity, float hoursPassed)
        {
            JitAcc.MotiveCov(10);
            if (Yield) Simulator.Sleep(1);
            return currentValue * 0.01f + hoursPassed * (float)commodity;
        }
        public override IGameObject Container { get { return null; } }
        public override string Name { get { return "FxMotivePosture"; } }
        public override bool PerformIdleLogic { get { return false; } }
        protected override void PopulateInteractions() { }
        public override float Satisfaction(CommodityKind condition, IGameObject target) { return condition == CommodityKind.Relaxing ? 1f : 0f; }
        public override InteractionInstance GetStandingTransition() { return null; }
        public override ScriptPosture GetSacsPostureParameter() { return default(ScriptPosture); }
        public override void Shoo(bool yield, List<Sim> shooedSims) { }
        public override Posture OnReset(IGameObject objectBeingReset) { return null; }
        public override bool AllowsReactionOverlay() { return false; }
        public override bool AllowsNormalSocials() { return false; }
        public override bool AllowsRouting() { return false; }
    }
}
