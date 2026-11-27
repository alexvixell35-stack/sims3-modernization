using System;
using System.Collections.Generic;
using System.Reflection;
using Ts3Fixes;

// stand-ins for the game's classes: the scan is told their names
public class TDesc
{
    public ulong mId;
    public ulong Id { get { return mId; } set { mId = value; } }
}
public class THouse
{
    public static List<THouse> sList = new List<THouse>();
    public List<TDesc> mAll = new List<TDesc>();
    public List<TDesc> All { get { return mAll; } }
    public bool Skip;
    public int Loose = -1; // test bookkeeping: the index's rebuild count at the last unhooked removal here
    public void Add(TDesc d) { }
}

static class ScanCases
{
    // no writer
    public static int ReadGetter(THouse h) { int n = 0; foreach (TDesc d in h.All) n += (int)d.Id; return n + h.All.Count; }
    public static int ReadAlias(THouse h) { List<TDesc> l = h.All; int n = l.Count; for (int i = 0; i < l.Count; i++) n += (int)l[i].Id; return n; }
    public static void RemoveGetter(THouse h, TDesc d) { h.All.Remove(d); h.All.Clear(); }
    public static int ReadStatic() { int n = 0; foreach (THouse h in THouse.sList) n++; return n + THouse.sList.Count; }
    public static void RemoveStatic(THouse h) { THouse.sList.Remove(h); }
    public static List<TDesc> Copy(THouse h) { return new List<TDesc>(h.All); }
    public static TDesc FreshSetter(ulong id, TDesc from) { TDesc d = new TDesc(); d.Id = from.mId; return d; }
    // writers
    public static void AddGetter(THouse h, TDesc d) { h.All.Add(d); }
    public static void AddAlias(THouse h, TDesc d) { List<TDesc> l = h.All; if (l.Count < 8) l.Add(d); }
    public static void SortGetter(THouse h) { h.All.Reverse(); }
    public static List<TDesc> Leak(THouse h) { return h.All; }
    public static void AddStatic(THouse h) { THouse.sList.Add(h); }
    public static void InsertStatic(THouse h) { THouse.sList.Insert(0, h); }
    public static void SetterOnArg(TDesc d, ulong id) { d.Id = id; }
    public static void SetterOnField(THouse h, ulong id) { h.mAll[0].Id = id; }
    public static TDesc SetterAfterAdd(THouse h, ulong id) { TDesc d = new TDesc(); h.Add(d); d.Id = id; return d; }
    public static void StoreField(TDesc d, ulong id) { d.mId = id; }
    public static void StoreList(THouse h) { h.mAll = new List<TDesc>(new TDesc[1]); }
}

// stands for an EA class: virtual, so the scan does not follow it
public class TEa
{
    public virtual int Reads(List<TDesc> l) { return l.Count; }
    public virtual int Writes(List<TDesc> l) { l.Add(null); return 0; }
}

// a mod class that keeps a list it is given
public class TKeeper
{
    protected ICollection<TDesc> mItems;
    public TKeeper(ICollection<TDesc> items) { mItems = items; }
    public int KeeperReads() { int n = 0; foreach (TDesc d in mItems) n += (int)d.mId; return n + mItems.Count; }
}
public class TBadKeeper
{
    ICollection<TDesc> mBad;
    public TBadKeeper(ICollection<TDesc> items) { mBad = items; }
    public void KeeperWrites(TDesc d) { mBad.Add(d); }
}

// the read-only helper shapes mods use (docs/359 §5), each next to a real writer of the same shape
static class HelperCases
{
    // readers
    public static ICollection<TDesc> Wrap(THouse h) { if (h == null) return new List<TDesc>(); return h.All; }
    public static int UseWrap(THouse h) { int n = 0; foreach (TDesc d in Wrap(h)) n += (int)d.mId; return n + Wrap(h).Count; }
    public static ICollection<TDesc> WrapWrap(THouse h) { return Wrap(h); }
    public static int UseWrapWrap(THouse h) { return WrapWrap(h).Count; }
    static int CountEnum2(IEnumerable<TDesc> e, ref int a) { foreach (TDesc d in e) a++; return a; }
    static int CountEnum(IEnumerable<TDesc> e) { int a = 0; return CountEnum2(e, ref a); }
    public static int PassEnum(THouse h) { return CountEnum(h.All); }
    static int Deep3(List<TDesc> l) { return l.Count; }
    static int Deep2(List<TDesc> l, int x) { return Deep3(l) + x; }
    static int Deep1(List<TDesc> l) { List<TDesc> k = l; return Deep2(k, 1); }
    public static int PassDeep(THouse h) { return Deep1(h.All); }
    public static TKeeper Keep(THouse h) { return new TKeeper(h.All); }
    public static TDesc FindLambda(THouse h) { return h.All.Find(delegate(TDesc e) { return e.mId == 1; }); }
    static int Take(int a, IEnumerable<TDesc> e, int b) { return a + b; }
    public static int PassArith(THouse h, int a, int b) { return Take(a, h.All, (a * 2 | b) - 1); }
    public static int PassEa(THouse h, TEa ea) { List<TDesc> l = h.All; foreach (TDesc d in l) if (d == null) return 0; return ea.Reads(l); }
    public static int PassStatic(TEa ea) { return THouse.sList.Count + ea.GetHashCode(); }
    // writers
    public static void WrapAdd(THouse h, TDesc d) { Wrap(h).Add(d); }
    public static void WrapWrapAdd(THouse h, TDesc d) { WrapWrap(h).Add(d); }
    static void DeepAdd2(List<TDesc> l, TDesc d) { l.Insert(0, d); }
    static void DeepAdd1(List<TDesc> l, TDesc d) { DeepAdd2(l, d); }
    public static void PassDeepAdd(THouse h, TDesc d) { DeepAdd1(h.All, d); }
    public static TBadKeeper KeepBad(THouse h) { return new TBadKeeper(h.All); }
    public static int PassEaWrites(THouse h, TEa ea) { return ea.Writes(h.All); }
    static void TakeList(int a, ICollection<TDesc> c) { c.Add(null); }
    public static void PassCollection(THouse h) { TakeList(1, h.All); }
}

static class SimDescIndexTest
{
    static int sFails, sChecks;
    static void Check(bool ok, string what) { sChecks++; if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    sealed class World : DescIndexCore.IWorld
    {
        public List<THouse> Houses = new List<THouse>();
        public int HouseCount { get { return Houses.Count; } }
        public object House(int slot) { return Houses[slot]; }
        public bool Skipped(object house) { return ((THouse)house).Skip; }
        public int MemberCount(object house) { return ((THouse)house).mAll.Count; }
        public object Member(object house, int slot) { return ((THouse)house).mAll[slot]; }
        public ulong Id(object desc) { return ((TDesc)desc).mId; }

        // the game's walk: first match in order, skipped households not walked; throws on a null like the game
        public TDesc Walk(ulong id, out int walked)
        {
            walked = 0;
            foreach (THouse h in Houses)
            {
                if (h.Skip) continue;
                foreach (TDesc d in h.mAll) { walked++; if (d.mId == id) return d; }
            }
            return null;
        }
    }

    static int sHits, sAbsents, sGame, sLooseAt; // sLooseAt: the rebuild count at the last unhooked removal (walk lengths are an estimate until the next rebuild)

    static void Probe(World w, DescIndexCore ix, Random rnd, bool inWindow)
    {
        ulong id = (ulong)rnd.Next(0, 64);
        object o; int walked;
        int r = ix.Lookup(id, w, out o, out walked);
        if (inWindow) { Check(r == DescIndexCore.Game && ix.Why == DescIndexCore.WhyWriter, "a lookup inside a writer's window is the game's"); return; }
        if (r == DescIndexCore.Game) { sGame++; return; }
        TDesc want = null; int ww = 0; bool threw = false;
        try { want = w.Walk(id, out ww); } catch (NullReferenceException) { threw = true; }
        Check(!threw, "an answer where the game's walk throws (id " + id + ")");
        if (r == DescIndexCore.Hit) { sHits++; Check(ReferenceEquals(o, want), "hit differs from the walk (id " + id + ")"); Check(ix.Rebuilds <= sLooseAt || walked == ww, "hit walk length " + walked + " vs " + ww); }
        else { sAbsents++; Check(want == null && o == null, "absent but the walk finds it (id " + id + ")"); Check(ix.Rebuilds <= sLooseAt || walked == ww, "absent walk length " + walked + " vs " + ww); }
    }

    static void RandomRun(int seed, bool nulls)
    {
        Random rnd = new Random(seed);
        World w = new World();
        DescIndexCore ix = new DescIndexCore();
        sHits = sAbsents = sGame = 0; sLooseAt = -1;
        for (int i = 0; i < 6; i++) { THouse h = new THouse(); w.Houses.Add(h); for (int k = 0; k < 4; k++) { TDesc d = new TDesc(); d.mId = (ulong)rnd.Next(1, 60); h.mAll.Add(d); } }
        for (int step = 0; step < 6000; step++)
        {
            int op = rnd.Next(200);
            THouse hh = w.Houses.Count > 0 ? w.Houses[rnd.Next(w.Houses.Count)] : null;
            if (op < 6 && hh != null)
            {   // hooked add (window), sometimes a duplicate id, with a lookup inside the window
                ix.Open();
                TDesc d = new TDesc(); d.mId = (ulong)rnd.Next(1, 60); hh.mAll.Insert(rnd.Next(hh.mAll.Count + 1), d);
                Probe(w, ix, rnd, true);
                ix.Close(true);
            }
            else if (op < 9) { ix.Open(); ix.Close(false); } // a writer that changed nothing
            else if (op < 14 && hh != null && hh.mAll.Count > 0) { hh.mAll.RemoveAt(rnd.Next(hh.mAll.Count)); if (rnd.Next(2) == 0) ix.Note(); else { sLooseAt = ix.Rebuilds; hh.Loose = ix.Rebuilds; } } // removal, hooked or not
            else if (op < 15 && hh != null) { hh.mAll.Clear(); sLooseAt = ix.Rebuilds; hh.Loose = ix.Rebuilds; } // unhooked clear (a mod)
            else if (op < 17 && hh != null && w.Houses.Count > 3) { w.Houses.Remove(hh); if (rnd.Next(2) == 0) ix.Note(); } // household removed, hooked or not
            else if (op < 19 && w.Houses.Count < 12) { w.Houses.Add(new THouse()); } // constructor: an empty household joins unseen
            else if (op < 22 && w.Houses.Count < 12)
            {   // a filled household placed: hooked window
                ix.Enter();
                THouse h = new THouse(); for (int k = 0; k < 3; k++) { TDesc d = new TDesc(); d.mId = (ulong)rnd.Next(1, 60); h.mAll.Add(d); }
                w.Houses.Insert(rnd.Next(w.Houses.Count + 1), h);
                ix.Exit();
            }
            else if (op < 25 && hh != null && hh.mAll.Count > 0)
            {   // id change through a hooked body
                TDesc d = hh.mAll[rnd.Next(hh.mAll.Count)];
                if (d != null) { ix.Open(); ulong old = d.mId; d.mId = (ulong)rnd.Next(1, 60); ix.Close(d.mId != old); }
            }
            else if (op < 27 && hh != null) { foreach (THouse h in w.Houses) h.Skip = false; if (rnd.Next(3) > 0) hh.Skip = true; sLooseAt = ix.Rebuilds; } // the previous-traveler household moves, unhooked
            else if (op < 28 && nulls && hh != null) { ix.Open(); hh.mAll.Add(null); ix.Close(true); }
            else if (op < 30 && nulls) { foreach (THouse h in w.Houses) { h.mAll.RemoveAll(delegate(TDesc d) { return d == null; }); h.Loose = ix.Rebuilds; } sLooseAt = ix.Rebuilds; }
            else if (op == 30) { ix.Reset(); }
            else if (op < 33 && hh != null)
            {   // an add no hook saw (a mod the scan missed), with an id nobody has: must never be answered "absent"
                // (not when an unhooked removal in the same household came since the last rebuild: the one shape the count check cannot see)
                bool hidden = hh.Loose == ix.Rebuilds;
                TDesc d = new TDesc(); d.mId = (ulong)(1000 + step); hh.mAll.Add(d); sLooseAt = ix.Rebuilds;
                object o2; int w2;
                int r2 = ix.Lookup(d.mId, w, out o2, out w2);
                Check(hidden || hh.Skip || r2 != DescIndexCore.Absent, "an unhooked add answered absent");
                if (r2 == DescIndexCore.Absent) { hh.mAll.Remove(d); } // the hidden shape: take it back so later probes compare a state the index may answer
            }
            else if (op == 33) { ix.Drop(); }
            for (int q = 0; q < 3; q++) Probe(w, ix, rnd, false);
        }
        Check(sHits > 1500, "seed " + seed + ": the index answers hits (" + sHits + ")");
        Check(sAbsents > 300, "seed " + seed + ": the index answers misses (" + sAbsents + ")");
        Check(sGame < sHits + sAbsents, "seed " + seed + ": fewer lookups left to the game than answered (" + sGame + " of " + (sGame + sHits + sAbsents) + ")");
        Check(ix.Rebuilds > 10 && ix.Rebuilds < 3000, "seed " + seed + ": rebuilds bounded (" + ix.Rebuilds + ")");
        Check(ix.Entries <= 64 && ix.HighWater <= 64, "seed " + seed + ": entries bounded by the ids in the world");
    }

    static void Fixed()
    {
        World w = new World();
        DescIndexCore ix = new DescIndexCore();
        THouse a = new THouse(), b = new THouse();
        TDesc a1 = new TDesc { mId = 1 }, a2 = new TDesc { mId = 2 }, b1 = new TDesc { mId = 2 }, b2 = new TDesc { mId = 3 };
        a.mAll.Add(a1); a.mAll.Add(a2); b.mAll.Add(b1); b.mAll.Add(b2); w.Houses.Add(a); w.Houses.Add(b);
        object o; int n;
        Check(ix.Lookup(2, w, out o, out n) == DescIndexCore.Game && ix.Why == DescIndexCore.WhyDirty, "first lookup after a reset is the game's");
        Check(ix.Lookup(2, w, out o, out n) == DescIndexCore.Hit && o == a2 && n == 2, "duplicate id: the first in walk order");
        Check(ix.Lookup(3, w, out o, out n) == DescIndexCore.Hit && o == b2 && n == 4, "walk length to the last one");
        Check(ix.Lookup(9, w, out o, out n) == DescIndexCore.Absent && n == 4, "absent: the whole walk");
        Check(ix.Rebuilds == 1 && ix.Entries == 3, "one rebuild, three ids");
        a.mAll.Remove(a2); // unhooked removal: the duplicate behind it is now the game's answer
        Check(ix.Lookup(2, w, out o, out n) == DescIndexCore.Game && ix.Why == DescIndexCore.WhyMoved, "a hit whose slot moved is the game's");
        Check(ix.Lookup(2, w, out o, out n) == DescIndexCore.Hit && o == b1 && n == 2, "rebuilt: the next duplicate");
        a1.mId = 7; // an id changed with no hook: its old entry no longer verifies
        Check(ix.Lookup(1, w, out o, out n) == DescIndexCore.Game, "a hit whose id changed is the game's");
        ix.Lookup(1, w, out o, out n);
        a.Skip = true; // the household becomes the skipped one, no hook
        Check(ix.Lookup(7, w, out o, out n) == DescIndexCore.Game, "a hit in a household that is now skipped is the game's");
        Check(ix.Lookup(7, w, out o, out n) == DescIndexCore.Absent && n == 2, "skipped household is not walked");
        a.Skip = false; // and back: its members are walked again
        Check(ix.Lookup(7, w, out o, out n) == DescIndexCore.Game, "the skipped household seen at the build is walked again: the game's");
        Check(ix.Lookup(7, w, out o, out n) == DescIndexCore.Hit && o == a1, "rebuilt with it");
        int moved = ix.Moved;
        w.Houses.Add(new THouse()); // count moved
        Check(ix.Lookup(3, w, out o, out n) == DescIndexCore.Game && ix.Moved == moved + 1, "a changed household count drops the snapshot");
        ix.Lookup(3, w, out o, out n);
        ix.Open(); ix.Close(false);
        Check(ix.Lookup(3, w, out o, out n) == DescIndexCore.Hit, "a writer that wrote nothing keeps the snapshot");
        ix.Enter();
        Check(ix.InWriter && ix.Lookup(3, w, out o, out n) == DescIndexCore.Game, "inside a window");
        ix.Exit();
        Check(ix.Lookup(3, w, out o, out n) == DescIndexCore.Game && ix.Lookup(3, w, out o, out n) == DescIndexCore.Hit, "after a window: one game lookup, then rebuilt");
        b.mAll.Add(null); ix.Note(); ix.Lookup(3, w, out o, out n);
        Check(ix.Lookup(3, w, out o, out n) == DescIndexCore.Game && ix.Why == DescIndexCore.WhyBroken && ix.Entries == 0, "a null member: every lookup is the game's");
        b.mAll.RemoveAt(b.mAll.Count - 1); ix.Note(); ix.Lookup(3, w, out o, out n); ix.Lookup(3, w, out o, out n);
        TDesc late = new TDesc { mId = 77 }; b.mAll.Add(late); // an add no hook saw
        Check(ix.Lookup(77, w, out o, out n) == DescIndexCore.Game && ix.Why == DescIndexCore.WhyMoved, "a miss after an unhooked add is the game's (member sum moved)");
        Check(ix.Lookup(77, w, out o, out n) == DescIndexCore.Hit && o == late, "rebuilt with the added one");
        ix.Enter(); ix.Drop();
        Check(ix.Entries == 0 && ix.InWriter, "drop holds nothing and keeps the open window");
        ix.Exit();
        ix.Reset();
        Check(ix.Entries == 0 && !ix.InWriter, "reset holds nothing");

        UrnSet u = new UrnSet();
        object x = new object(), y = new object();
        u.Add(5, x); u.Add(5, x); u.Add(0, y); u.Add(6, null);
        Check(u.Count == 1 && !u.Seeded, "urn set: one urn, id 0 and null refused, not seeded by adds");
        u.Seed(new ulong[] { 7, 8 }, new object[] { x, y });
        Check(u.Count == 2 && u.Seeded && !u.Stale && u.Seeds == 1, "seed replaces");
        Check(u.Drop(7) && !u.Drop(7) && u.Count == 1, "drop once");
        Check(!u.Add(0, y) && !u.Seeded && u.Count == 1, "an urn without an id: the set is no superset, not seeded");
        u.Seed(new ulong[] { 7, 0 }, new object[] { x, y });
        Check(!u.Seeded && u.Count == 1 && u.Seeds == 2, "a seed that holds an urn without an id stays not seeded");
        u.Seed(new ulong[] { 7, 8 }, new object[] { x, y });
        u.Stale = true; u.Clear();
        Check(u.Count == 0 && !u.Seeded && !u.Stale && u.HighWater == 2 && u.Seeds == 3, "clear holds nothing");
    }

    static int Scan(string method, bool targets)
    {
        Dictionary<string, int> f = new Dictionary<string, int>();
        f["THouse::sList"] = 1; f["THouse::mAll"] = 1; f["TDesc::mId"] = 1;
        Dictionary<string, bool> l = new Dictionary<string, bool>();
        l["THouse::sList"] = true; l["THouse::mAll"] = true; l["THouse::get_All"] = true;
        Dictionary<string, int> calls = new Dictionary<string, int>();
        calls["THouse::get_All"] = 1; calls["TDesc::set_Id"] = 1;
        DescWriterScan.Core = delegate(Assembly a) { return a == typeof(object).Assembly; };
        DescWriterScan.CallTargets = targets ? calls : null; DescWriterScan.AliasLocals = targets;
        try
        {
            bool bad;
            MethodInfo m = typeof(ScanCases).GetMethod(method);
            int k = DescWriterScan.ScanMethod(m, DescWriterScan.ReflectionBody, new Dictionary<Module, Dictionary<int, MemberInfo>>(), f, l, out bad);
            Check(!bad, method + " readable");
            return k;
        }
        finally { DescWriterScan.CallTargets = null; DescWriterScan.AliasLocals = false; }
    }

    static void ScanTests()
    {
        foreach (string s in new string[] { "ReadGetter", "ReadAlias", "RemoveGetter", "ReadStatic", "RemoveStatic", "Copy", "FreshSetter" })
            Check(Scan(s, true) == 0, "scan: " + s + " is no writer" + (DescWriterScan.Hit != null ? " (" + DescWriterScan.Hit + ")" : ""));
        foreach (string s in new string[] { "AddGetter", "AddAlias", "SortGetter", "Leak", "AddStatic", "InsertStatic", "SetterOnArg", "SetterOnField", "SetterAfterAdd", "StoreField", "StoreList" })
            Check(Scan(s, true) == 1, "scan: " + s + " is a writer");
        // without the call targets (every other scan): getters and setters are not seen, fields as before
        Check(Scan("AddGetter", false) == 0 && Scan("SetterOnArg", false) == 0, "scan: call targets off by default");
        Check(Scan("AddStatic", false) == 1 && Scan("ReadStatic", false) == 0 && Scan("StoreField", false) == 1, "scan: field targets unchanged");
    }

    // the whole-assembly scan as SimDescIndex.cs runs it: repeated until no new list getter / kept field
    static void HelperTests()
    {
        Dictionary<string, int> f = new Dictionary<string, int>();
        f["THouse::sList"] = 1; f["THouse::mAll"] = 1; f["TDesc::mId"] = 1;
        Dictionary<string, bool> l = new Dictionary<string, bool>();
        l["THouse::sList"] = true; l["THouse::mAll"] = true; l["THouse::get_All"] = true;
        Dictionary<string, int> calls = new Dictionary<string, int>();
        calls["THouse::get_All"] = 1; calls["TDesc::set_Id"] = 1;
        DescWriterScan.Core = delegate(Assembly a) { return a == typeof(object).Assembly; };
        DescWriterScan.CallTargets = calls; DescWriterScan.AliasLocals = true;
        DescWriterScan.ReadCallees = new Dictionary<string, bool>();
        DescWriterScan.ReadCallees["TEa::Reads"] = true;
        DescWriterScan.Wrappers = new Dictionary<string, bool>();
        DescWriterScan.Derived = new Dictionary<string, bool>();
        DescWriterScan.Result r = null;
        int passes = 0;
        try
        {
            for (; passes < 5; passes++)
            {
                r = new DescWriterScan.Result();
                DescWriterScan.NewWrappers = 0;
                DescWriterScan.ScanTypes("test", new Type[] { typeof(HelperCases), typeof(TKeeper), typeof(TBadKeeper) }, null, null, DescWriterScan.ReflectionBody, f, l, r, 100);
                if (DescWriterScan.NewWrappers == 0) break;
            }
            Check(passes >= 1 && passes < 4, "helpers: the scan settles (" + (passes + 1) + " passes)");
            Check(DescWriterScan.Wrappers.ContainsKey("HelperCases::Wrap") && DescWriterScan.Wrappers.ContainsKey("HelperCases::WrapWrap"), "helpers: both list getters found");
            Check(DescWriterScan.Derived.ContainsKey("TKeeper::mItems") && DescWriterScan.Derived.ContainsKey("TBadKeeper::mBad"), "helpers: both kept fields found");
        }
        finally
        {
            DescWriterScan.CallTargets = null; DescWriterScan.AliasLocals = false;
            DescWriterScan.ReadCallees = null; DescWriterScan.Wrappers = null; DescWriterScan.Derived = null;
        }
        Dictionary<string, bool> hit = new Dictionary<string, bool>();
        foreach (string line in r.Log) { string[] w = line.Split(' '); hit[w[1].Substring(w[1].IndexOf("::") + 2)] = true; }
        Check(r.Unreadable == 0, "helpers: all readable");
        foreach (string s in new string[] { "Wrap", "UseWrap", "WrapWrap", "UseWrapWrap", "CountEnum2", "CountEnum", "PassEnum", "Deep3", "Deep2", "Deep1", "PassDeep", "Keep",
            "KeeperReads", ".ctor", "FindLambda", "Take", "PassArith", "PassEa", "PassStatic", "KeepBad", "DeepAdd2", "DeepAdd1", "TakeList" })
            Check(!hit.ContainsKey(s), "helpers: " + s + " is no writer");
        foreach (string s in new string[] { "WrapAdd", "WrapWrapAdd", "PassDeepAdd", "KeeperWrites", "PassEaWrites", "PassCollection" })
            Check(hit.ContainsKey(s), "helpers: " + s + " is a writer");
        Check(hit.Count == 6, "helpers: 6 writers, found " + hit.Count);

        // a method read by hand is covered only while its IL hash is the one read
        MethodInfo m = typeof(HelperCases).GetMethod("PassCollection");
        ulong h = AutShadowCore.MethodIlHash(AutShadowCore.SigKey(m), m, new Dictionary<Module, Dictionary<int, string>>());
        Dictionary<string, bool> al = new Dictionary<string, bool>();
        Check(DescWriterScan.CoverByHash(typeof(HelperCases).Assembly, "HelperCases", "PassCollection", h ^ 1, al) == 0 && al.Count == 0, "cover by hash: another body is not covered");
        Check(DescWriterScan.CoverByHash(typeof(HelperCases).Assembly, "HelperCases", "PassCollection", h, al) == 1 && al.ContainsKey("HelperCases::PassCollection"), "cover by hash: the body read is covered");
        Check(DescWriterScan.CoverByHash(typeof(HelperCases).Assembly, "NoSuchType", "X", h, al) == 0, "cover by hash: a missing type covers nothing");
    }

    static int Main()
    {
        Fixed();
        for (int s = 1; s <= 12; s++) RandomRun(s, s % 3 == 0);
        ScanTests();
        HelperTests();
        Console.WriteLine(sFails == 0 ? "simdescindex self-check ok (" + sChecks + " checks)" : "simdescindex self-check: " + sFails + " FAIL of " + sChecks);
        return sFails == 0 ? 0 : 1;
    }
}
