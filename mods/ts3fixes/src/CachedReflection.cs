using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Cached per-Type answers in the save serializer (docs/26 "Cached reflection"). For every object it writes, the
    // PersistWriter asks Persist.GetPersistedTypeCode(type) (a type classification internal call, Type.GetTypeCode with
    // UnderlyingSystemType/IsSystemType/TypeHandle.Value, up to two IsTypeAssignableFrom internal calls, then a Hashtable
    // lookup keyed by Type whose GetHashCode reads TypeHandle.Value, all interpreted) and PersistedClassDB.GetPersistedClass
    // (the same Hashtable lookup), several times per object. For one DB (one save) the answer per Type never changes after
    // the first call, and later calls have no side effects (TypeCache.cs says why), so the hooks answer repeats from a
    // TypeCache keyed by Type identity and run the game's method only on the first call per Type. Same answers, same class
    // index order, so the save bytes stay the same; the startup probe serializes a test graph with the cache off and on
    // and compares the bytes.
    //
    // PersistedClass.Init is hooked only to count running Inits (TypeCache.sBusy): nothing is stored while one runs.
    // A new DB (the game makes one per PersistWriter, so one per save) starts an empty cache, and the switch is read then,
    // so flipping it takes effect at the next save. Off: the hooks run the game's method.
    // Switch "cachedreflection" (tuning kCachedReflection, default off). Counters: [71] caches started with the switch on
    // (one per save, more if another DB is used in between), [72] answers from the cache, [73] answers stored.
    public static partial class Fixes
    {
        static bool sClassCacheOk, sCodeCacheOk; // patches in (with the Init hook) and the probe passed
        static int sCachedReflectionForce;       // probe only: 1 on, -1 off, 0 the switch

        static void ApplyCachedReflection()
        {
            Assembly sc = typeof(ScriptCore.PersistWriter).Assembly;
            Type db = sc.GetType("ScriptCore.PersistedClassDB"), pc = sc.GetType("ScriptCore.PersistedClass");
            if (db == null || pc == null) { AppliedLog().Add("CachedReflection: serializer classes not found, skipped"); return; }
            bool init = Patch(pc, "Init", null, typeof(PersistedClassHook), "InitHook", "InitOrig");
            bool cls = Patch(db, "GetPersistedClass", null, typeof(ClassDBHook), "GetClassHook", "GetClassOrig");
            bool code = Patch(typeof(ScriptCore.Persist), "GetPersistedTypeCode",
                new Type[] { typeof(Type), typeof(ReflectionTypeFlags).MakeByRefType(), db }, typeof(TypeCodeHook), "CodeHook", "CodeOrig");
            if (!init || !(cls || code)) { AppliedLog().Add("CachedReflection: not patched, off"); return; }
            int[] c = PatchCounters();
            TypeCache.sCounters = c;
            TypeCache.sHitSlot = 72;
            TypeCache.sStoreSlot = 73;
            string why;
            sClassCacheOk = cls;
            sCodeCacheOk = code;
            try { why = ProbeCachedReflection(); }
            catch (Exception e) { why = "failed: " + e; }
            finally { sCachedReflectionForce = 0; TypeCache.sCur = null; c[71] = c[72] = c[73] = 0; }
            if (why != null) sClassCacheOk = sCodeCacheOk = false;
            AppliedLog().Add("CachedReflection probe (class cache " + (cls ? "in" : "out") + ", type code cache " + (code ? "in" : "out") + "): " + (why ?? "ok"));
        }

        // Serializes the same graph through the game's PersistWriter with the cache off and on and compares the bytes.
        // Null when equal and the cache was used.
        static string ProbeCachedReflection()
        {
            int[] c = PatchCounters();
            sCachedReflectionForce = -1;
            byte[] off = ProbeSerialize();
            if (c[72] != 0 || c[73] != 0) return "cache used while off";
            sCachedReflectionForce = 1;
            byte[] on = ProbeSerialize();  // the hooks count [72]/[73] while it runs
            if (c[72] == 0 || c[73] == 0) return "cache not used (hits " + c[72] + ", stored " + c[73] + ")";
            if (off.Length != on.Length) return "length " + off.Length + " vs " + on.Length;
            for (int i = 0; i < off.Length; i++) if (off[i] != on[i]) return "bytes differ at " + i + " of " + off.Length;
            return null;
        }

        static byte[] ProbeSerialize()
        {
            ProbeNode root = new ProbeNode();
            for (int i = 0; i < 40; i++)
            {
                ProbeNode n = new ProbeNode();
                n.mId = i;
                n.mName = "n" + (i % 7);
                n.mKind = (ProbeKind)(i % 3);
                n.mPoint.mX = i * 0.5f;
                n.mPoint.mKind = (ProbeKind)(2 - i % 3);
                n.mKey = new ResourceKey((ulong)i, 0x319E4F1Du, (uint)(i & 3));
                n.mKinds = new ProbeKind[] { ProbeKind.A, (ProbeKind)(i % 3) };
                n.mNext = i > 0 ? root.mChildren[i - 1] : null;
                n.mSkipped = n;
                n.mBoxed = (i & 1) == 0 ? (object)(ProbeKind)(i % 3) : (object)i;
                root.mChildren.Add(n);
                root.mByName[n.mName + i] = n;
                root.mMixed.Add(i % 4 == 0 ? (object)n.mPoint : i % 4 == 1 ? (object)n.mKinds : i % 4 == 2 ? (object)n.mName : n);
            }
            ScriptCore.PersistWriter pw = new ScriptCore.PersistWriter();
            FastStreamWriter w = new FastStreamWriter();
            try
            {
                pw.Begin();
                pw.AddObject(root);
                pw.End(w);
                return w.ToArray();
            }
            finally { pw.Dispose(); w.Dispose(); }
        }

        public enum ProbeKind { A, B, C }

        [Persistable]
        public struct ProbePoint
        {
            public float mX;
            public ProbeKind mKind;
        }

        [Persistable]
        public class ProbeNode
        {
            public int mId;
            public string mName;
            public ProbeKind mKind;
            public ProbePoint mPoint;
            public ResourceKey mKey;
            public ProbeKind[] mKinds;
            public ProbeNode mNext;
            [Persistable(false)] public ProbeNode mSkipped;
            public object mBoxed;
            public List<ProbeNode> mChildren = new List<ProbeNode>();
            public Dictionary<string, ProbeNode> mByName = new Dictionary<string, ProbeNode>();
            public List<object> mMixed = new List<object>();
        }

        // Cache of the DB seen now; the switch is read once per DB.
        static TypeCache NewTypeCache(object db)
        {
            bool on = sCachedReflectionForce != 0 ? sCachedReflectionForce > 0 : ShortcutOn("cachedreflection");
            TypeCache k = new TypeCache(db, on);
            TypeCache.sCur = k;
            if (on) PatchCounters()[71]++;
            return k;
        }

        // Swapped into ScriptCore.PersistedClass.Init. No instance fields.
        public class PersistedClassHook
        {
            public bool InitHook(Type type, object pclassDB)
            {
                TypeCache.sBusy++;
                try { return InitOrig(type, pclassDB); }
                finally { TypeCache.sBusy--; }
            }

            public bool InitOrig(Type type, object pclassDB) { return false; }
        }

        // Swapped into ScriptCore.PersistedClassDB.GetPersistedClass (`this` is the DB; returns a PersistedClass or null).
        // No instance fields.
        public class ClassDBHook
        {
            public object GetClassHook(Type type, bool requireClassReference)
            {
                if (!sClassCacheOk || type == null) return GetClassOrig(type, requireClassReference);
                object db = this;
                TypeCache k = TypeCache.sCur;
                if (k == null || k.Owner != db) k = NewTypeCache(db);
                if (!k.On) return GetClassOrig(type, requireClassReference);
                object pc;
                if (k.TryClass(type, requireClassReference, out pc)) return pc;
                pc = GetClassOrig(type, requireClassReference);
                k.StoreClass(type, requireClassReference, pc);
                return pc;
            }

            public object GetClassOrig(Type type, bool requireClassReference) { return null; }
        }

        // Swapped into ScriptCore.Persist.GetPersistedTypeCode(Type, ref ReflectionTypeFlags, PersistedClassDB). The game's
        // return type is PersistedTypeCode, a byte enum. Calls without a DB (the loader's) always run the game's method.
        public class TypeCodeHook
        {
            public static byte CodeHook(Type type, ref ReflectionTypeFlags typeFlags, object pclassDB)
            {
                if (!sCodeCacheOk || pclassDB == null || type == null) return CodeOrig(type, ref typeFlags, pclassDB);
                TypeCache k = TypeCache.sCur;
                if (k == null || k.Owner != pclassDB) k = NewTypeCache(pclassDB);
                if (!k.On) return CodeOrig(type, ref typeFlags, pclassDB);
                int code, flags;
                if (k.TryCode(type, out code, out flags)) { typeFlags = (ReflectionTypeFlags)flags; return (byte)code; }
                byte result = CodeOrig(type, ref typeFlags, pclassDB);
                k.StoreCode(type, result, (int)typeFlags);
                return result;
            }

            public static byte CodeOrig(Type type, ref ReflectionTypeFlags typeFlags, object pclassDB) { return 0; }
        }
    }
}
