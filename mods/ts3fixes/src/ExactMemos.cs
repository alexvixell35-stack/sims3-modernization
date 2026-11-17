using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;

namespace Ts3Fixes
{
    // Lane L4 1115: three exact memos/early-outs, each default off with its own tuning and switch. Same answer as the
    // game's method for every caller and every Sim (household and selected Sims included), so no scope gate. Off the sim
    // thread (the [AutShadow] worker) each hook runs the game's method (docs/128 G-M3). Tables: TickMemo.cs, offline
    // self-check tools/selftest/hotspots.
    //
    // permamemo (docs/245 §5 H, tuning kPermaMoodletMemo): BuffManager.IsPermaMoodlet(name) reads the static buff table
    // (a Dictionary<ulong, BuffInstance>, default comparer) and the entry's Buff flag. The answer is kept per name with the
    // BuffInstance and Buff it came from. Every writer of the table goes through ParseBuffData (Add: Count moves; the
    // newer-version overwrite: the parse generation moves, both hooked) and the flag is internal to the game assembly;
    // a hit also checks that the instance's public mBuff is still the kept Buff. Left out: writes by reflection (a mod
    // that sets the protected static table or the private BuffData flags directly); permamemoverify counts those.
    // Counters: [1014] hits, [1015] misses, [1016] verify mismatches, [1017] table clears (stamp moved).
    //
    // buffremovenoop (docs/95 §5.5, tuning kBuffRemoveNoOp): BuffManager.RemoveElement(BuffNames) on a buff the Sim does
    // not have, with no ref-count list at all and no delayed buff queued, does nothing (an enum-keyed ContainsKey that
    // boxes, an empty scan, a failed TryGetValue). The hook returns at once in exactly that case (BuffRemoveRule).
    // Counters: [1018] early returns, [1019] calls passed to the game's method.
    //
    // catnamecache (docs/96 B2/D2, tuning kCatalogNameCache): UserToolUtils.GetProductCatalogName(product) asks the
    // native product table and the string tables for the product's localized name (Overwatch's daily CleanupInventory
    // walks every inventory through GameObject.CatalogName). Kept per product object and native handle (CatalogNameMemo):
    // a disposed product has handle 0 and a reused handle belongs to another product object, both misses. Names change
    // only with new content or a locale change: both UI events clear the table, as does world quit.
    // Counters: [1020] hits, [1021] misses, [1022] verify mismatches, [1023] clears.
    public static partial class Fixes
    {
        static bool sPermaReady, sBuffRemoveReady, sCatNameReady;
        static Dictionary<ulong, BuffInstance> sBuffTable;
        static int sBuffParseGen;
        static readonly PermaMemo sPerma = new PermaMemo();
        static FieldInfo sBrCountList, sBrDelayed;
        static readonly CatalogNameMemo sCatNames = new CatalogNameMemo();

        static void ApplyExactMemos()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
            // permamemo: table reference, then every parse path, then the memo hook
            FieldInfo table = typeof(GenericManager<BuffNames, BuffInstance, BuffInstance>).GetField("sDictionary", st);
            sBuffTable = table != null ? table.GetValue(null) as Dictionary<ulong, BuffInstance> : null;
            if (sBuffTable == null) AppliedLog().Add("permamemo: static buff table not found, not patched");
            else
            {
                // ParseBuffData already ran (compiled) when the startup table was read before us: then the public parse
                // entries (new store buffs mid-session, test parses) carry the bump instead
                bool parse = Patch(typeof(BuffManager), "ParseBuffData", new Type[] { typeof(XmlDbData), typeof(bool) }, typeof(BuffParseHook), "ParseHook", "ParseOrig");
                if (!parse)
                    parse = Patch(typeof(BuffManager), "LoadNewlyAddedBuffRecords", new Type[] { typeof(ResourceKey[]) }, typeof(BuffParseHook), "NewHook", "NewOrig")
                        & Patch(typeof(BuffManager), "TestParseBuffData", new Type[] { typeof(XmlDbData) }, typeof(BuffParseHook), "TestHook", "TestOrig");
                if (!parse) AppliedLog().Add("permamemo: a buff parse path could not be hooked, memo not patched");
                else sPermaReady = Patch(typeof(BuffManager), "IsPermaMoodlet", new Type[] { typeof(BuffNames) }, typeof(PermaMemoHook), "PermaHook", "PermaOrig");
            }
            // buffremovenoop
            sBrCountList = typeof(BuffManager).GetField("mBuffCountList", inst);
            sBrDelayed = typeof(BuffManager).GetField("mDelayedBuffs", inst);
            if (sBrCountList == null || sBrDelayed == null || sBrDelayed.FieldType != typeof(ListSorted<BuffTimer>))
                AppliedLog().Add("buffremovenoop: BuffManager private members not found, not patched");
            else sBuffRemoveReady = Patch(typeof(BuffManager), "RemoveElement", new Type[] { typeof(BuffNames) }, typeof(BuffRemoveHook), "RemoveHook", "RemoveOrig");
            // catnamecache
            sCatNameReady = Patch(typeof(UserToolUtils), "GetProductCatalogName", new Type[] { typeof(BuildBuyProduct) }, typeof(CatalogNameHook), "NameHook", "NameOrig");
            if (sCatNameReady)
            {
                Sims3.UI.UIManager.NewContentInstalled += delegate(string m) { CatNameClear(); };
                Sims3.UI.UIManager.LocaleChanged += delegate { CatNameClear(); };
                World.OnWorldQuitEventHandler += delegate { CatNameClear(); sPerma.Clear(); };
            }
        }

        static void CatNameClear() { if (sCatNames.Clear()) PatchCounters()[1023]++; }

        public static class BuffParseHook
        {
            public static void ParseHook(XmlDbData data, bool bStore)
            {
                sBuffParseGen++;
                try { ParseOrig(data, bStore); }
                finally { sBuffParseGen++; }  // the memo must not keep an answer read during the parse
            }
            public static void ParseOrig(XmlDbData data, bool bStore) { } // receives BuffManager.ParseBuffData's original header

            public static void NewHook(ResourceKey[] keys) { sBuffParseGen++; try { NewOrig(keys); } finally { sBuffParseGen++; } }
            public static void NewOrig(ResourceKey[] keys) { } // receives BuffManager.LoadNewlyAddedBuffRecords's original header

            public static void TestHook(XmlDbData data) { sBuffParseGen++; try { TestOrig(data); } finally { sBuffParseGen++; } }
            public static void TestOrig(XmlDbData data) { } // receives BuffManager.TestParseBuffData's original header
        }

        public class PermaMemoHook
        {
            public bool PermaHook(BuffNames name)
            {
                if (!sPermaReady || OnWorker() || !ShortcutOn("permamemo")) return PermaOrig(name);
                int[] c = PatchCounters();
                if (sPerma.Stamp(sBuffTable.Count, sBuffParseGen)) c[1017]++;
                object kept, buff;
                bool ans;
                if (sPerma.TryGet((ulong)name, out kept, out buff, out ans) && (kept == null || ReferenceEquals(((BuffInstance)kept).mBuff, buff)))
                {
                    c[1014]++;
                    if (VerifyNow("permamemoverify"))
                    {
                        bool g = PermaOrig(name);
                        if (g != ans) c[1016]++;
                        return g;
                    }
                    return ans;
                }
                c[1015]++;
                bool res = PermaOrig(name);  // throws as the game's does (nothing kept)
                BuffInstance bi = null;
                if (name != BuffNames.Undefined) sBuffTable.TryGetValue((ulong)name, out bi);
                sPerma.Store((ulong)name, bi, bi != null ? bi.mBuff : null, res);
                return res;
            }
            public bool PermaOrig(BuffNames name) { return false; } // receives BuffManager.IsPermaMoodlet's original header
        }

        public class BuffRemoveHook
        {
            public void RemoveHook(BuffNames guid)
            {
                // docs/622 §2 buffremovemerge: the RemoveElement(ulong, bool) hook does the one-lookup no-op test
                if (BrmReady() && SwOn(kSwBrMerge) && !OnWorker()) { RemoveOrig(guid); return; }
                if (sBuffRemoveReady && !OnWorker() && SwOn(kSwBuffRemoveNoop))
                {
                    BuffManager bm = (BuffManager)(object)this;
                    // GenericManager.HasElement(ulong) = mValues.ContainsKey: the test the base RemoveElement makes
                    if (!((GenericManager<BuffNames, BuffInstance, BuffInstance>)bm).HasElement((ulong)guid))
                    {
                        ICollection counts = sBrCountList.GetValue(bm) as ICollection;
                        ListSorted<BuffTimer> delayed = sBrDelayed.GetValue(bm) as ListSorted<BuffTimer>;
                        if (BuffRemoveRule.IsNoOp(false, counts == null, counts != null ? counts.Count : 0, delayed == null, delayed != null ? delayed.Count : 0))
                        {
                            PatchCounters()[1018]++;
                            return;
                        }
                    }
                    PatchCounters()[1019]++;
                }
                RemoveOrig(guid);
            }
            public void RemoveOrig(BuffNames guid) { } // receives BuffManager.RemoveElement(BuffNames)'s original header
        }

        public static class CatalogNameHook
        {
            public static string NameHook(BuildBuyProduct product)
            {
                if (!sCatNameReady || product == null || OnWorker() || !ShortcutOn("catnamecache")) return NameOrig(product);
                int[] c = PatchCounters();
                uint h = unchecked((uint)product.GetHashCode()); // BuildBuyProduct.GetHashCode = (int)Handle (Handle is internal)
                string name;
                if (sCatNames.TryGet(product, h, out name))
                {
                    c[1020]++;
                    if (VerifyNow("catnamecacheverify"))
                    {
                        string g = NameOrig(product);
                        if (g != name) c[1022]++;
                        return g;
                    }
                    return name;
                }
                c[1021]++;
                name = NameOrig(product);
                sCatNames.Store(product, h, name);
                return name;
            }
            public static string NameOrig(BuildBuyProduct product) { return null; } // receives UserToolUtils.GetProductCatalogName's original header
        }
    }
}
