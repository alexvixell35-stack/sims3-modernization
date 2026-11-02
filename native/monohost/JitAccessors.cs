// Round 8 B6 (docs/207 §9.1): offline fixtures for the never-yielding Sims3Gameplay* accessors of docs/207 §9. Each method
// builds the objects an accessor reads (real constructors, private fields set by reflection; this mscorlib has no
// GetUninitializedObject) and calls it once for input i. tools/jit-corpus.ts wraps every call in a corpus case, so the
// value or the exception is hashed interpreted twice and compiled once (MONOHOST_JITCORPUS=1, docs/207 §10). C# 2.
// Compiled against the game's Sims3GameplaySystems.dll and ScriptCore.dll (build.cmd). Hand-written, not generated.
using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.ObjectComponents;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace MonoHostTest
{
    // a concrete GameObject: GameObject's constructor only sets a delegate, ScriptObject's does nothing
    public class FxObject : GameObject { }
    public class FxLot : Lot { }
    public class FxCompA : ObjectComponent { public override Type BaseType { get { return typeof(FxCompA); } } }
    public class FxCompB : ObjectComponent { public override Type BaseType { get { return typeof(FxCompB); } } }
    public class FxCompC : ObjectComponent { public override Type BaseType { get { return typeof(FxCompC); } } }
    public class FxRouting : RoutingComponent { public override Route CreateRoute() { return null; } }

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
    }
}
