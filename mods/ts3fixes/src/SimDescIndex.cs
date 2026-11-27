using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Tutorial;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;

namespace Ts3Fixes
{
    // simdescindex (docs/359, default OFF; candidate of docs/356 §6): SimDescription.Find(id) answered from a snapshot
    // of the game's own walk (DescIndexCore, SimDescIndexCore.cs) instead of walking every household, and its miss path
    // answered without the world-wide Urnstone query (UrnSet). A pure lookup: the answer is the object the game's walk
    // returns, for every Sim (household and selected Sims included: nothing about a Sim changes), in every game state
    // (pause, CAS, build mode: no gate needed, the writers below keep it exact there too).
    //
    // Exact by construction, per writer of what the walk reads [verified decompiled, docs/359 §3]:
    //   adds (a hook opens a window, lookups inside it are the game's, its end marks the snapshot dirty):
    //     Household.Members.Add (every Household.Add / AddSilent / AddTemporary and Members.ImportContent go through it),
    //     Members.PostLoadFixup, SimDescription.MakeUniqueId, SimDescription.ImportContent (the id writers; the
    //     inlined id setter has three EA callers, all on a description created in the same method).
    //   removals, household list changes, the previous-traveler household: exact without a hook (a hit is re-checked at
    //     its slot, the household count is compared on every lookup); the hooks on Members.RemoveAt / DisposeEachMember,
    //     Household.ClearSilent / Cleanup / Startup / Shutdown / the four Destroy*Household / CreatePreviousTravelerHousehold
    //     and Bin.PlaceHousehold only mark the snapshot dirty at once.
    //   world load, travel, quit: held from load start to load finish and at quit (the save's lists arrive without a call).
    //   mods: the IL of every loaded non-EA assembly is scanned once (DescWriterScan with call targets) for an add to a
    //     member list or the household list, a store of either list or of the id, and the id setter on anything but a
    //     description created in that method; any hit turns the switch's effect off for the session (untrusted, logged).
    //     Reads are no hits: IEnumerable parameters, callees followed 3 deep, helpers that return a list and mod fields
    //     that keep one (followed as lists themselves), two EA callees read by hand (docs/359 §5).
    // Urnstones: every GameObject.Init is noted (DrInitHook), a destroy drops the urn once the world no longer resolves
    // it; an urn that holds the id is left to the game (the world's order decides between two of them).
    // With the switch on every 64th answered lookup (simdescindexverify: every one) also runs the game's walk and
    // compares the object ([1913] must stay 0); the game's answer is the one returned.
    // Leaks: references to descriptions, households and urns only between rebuilds; cleared at load start, load finish
    // and quit; sizes in [1905]-[1907].
    // [1899] lookups with the switch on, [1900] hits, [1901] misses answered, [1902] left to the game's walk, [1903] of
    // those: an urn holds the id, [1904] rebuilds, [1905] entries, [1906] high-water entries, [1907] urns held,
    // [1908] writer notes, [1909] snapshots dropped by the slot / count check (a writer no hook saw), [1910] descriptions
    // the game's walk would have read, in thousands, [1911] world-wide urn queries not made, [1912] shadow compares,
    // [1913] shadow mismatches (findMismatch, must be 0), [1914] game walk us in shadow hits, [1915] descriptions those
    // walks read, [1916] game walk us in shadow misses, [1917] shadow misses, [1918] mod writer methods found (untrusted
    // when > 0), [1919] errors, [1920] ready bits (1 hooks in, 2 urn hook in, 4 scan done), [1921] urn seeds, [1922] spare.
    public static partial class Fixes
    {
        const int kSdi = 1899;
        static readonly int kSwSdi = SwSlot("simdescindex"), kSwSdiVerify = SwSlot("simdescindexverify");
        static readonly DescIndexCore sSdi = new DescIndexCore();
        static readonly UrnSet sSdiUrns = new UrnSet();
        static readonly SdiWorld sSdiWorld = new SdiWorld();
        static bool sSdiReady, sSdiHold = true, sSdiScanned, sSdiUrnOk;
        static int sSdiUntrusted, sSdiAnswered, sSdiLogged, sSdiStaleMisses;
        static long sSdiWalked;
        static StopWatch sSdiWatch;

        sealed class SdiWorld : DescIndexCore.IWorld
        {
            public int HouseCount { get { return Household.sHouseholdList.Count; } }
            public object House(int slot) { return Household.sHouseholdList[slot]; }
            public bool Skipped(object house) { return ((Household)house).IsPreviousTravelerHousehold; }
            public int MemberCount(object house) { return ((Household)house).AllSimDescriptions.Count; }
            public object Member(object house, int slot) { return ((Household)house).AllSimDescriptions[slot]; }
            public ulong Id(object desc) { return ((SimDescription)desc).SimDescriptionId; }
        }

        static void ApplySimDescIndex()
        {
            // the bodies that were read (Steam 1.67): another build of the game (EA 1.69, a core mod's replacement) is not patched
            string differs = SdiBodiesRead();
            if (differs != null) { AppliedLog().Add("simdescindex: " + differs + " is not the body that was read, not patched"); return; }
            // the writers that can add: all four must be in before the lookup is swapped
            bool adds = Patch(typeof(Household.Members), "Add", new Type[] { typeof(SimDescription), typeof(IActor) }, typeof(SdiMembersHook), "AddHook", "AddOrig")
                & Patch(typeof(Household.Members), "PostLoadFixup", Type.EmptyTypes, typeof(SdiMembersHook), "FixupHook", "FixupOrig")
                & Patch(typeof(SimDescription), "MakeUniqueId", Type.EmptyTypes, typeof(SdiDescHook), "UniqueHook", "UniqueOrig")
                & Patch(typeof(SimDescription), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) }, typeof(SdiDescHook), "ImportHook", "ImportOrig");
            if (!adds) { AppliedLog().Add("simdescindex: a writer that adds could not be hooked, lookup not patched"); return; }
            // removals and list changes: exact without these (slot and count checks); they only drop the snapshot sooner
            Patch(typeof(Household.Members), "RemoveAt", new Type[] { typeof(int) }, typeof(SdiMembersHook), "RemoveAtHook", "RemoveAtOrig");
            Patch(typeof(Household.Members), "DisposeEachMember", Type.EmptyTypes, typeof(SdiMembersHook), "DisposeEachHook", "DisposeEachOrig");
            Patch(typeof(Household), "ClearSilent", Type.EmptyTypes, typeof(SdiHouseHook), "ClearHook", "ClearOrig");
            Patch(typeof(Household), "Cleanup", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "CleanupHook", "CleanupOrig");
            Patch(typeof(Household), "Startup", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "StartupHook", "StartupOrig");
            Patch(typeof(Household), "Shutdown", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "ShutdownHook", "ShutdownOrig");
            Patch(typeof(Household), "DestroyPetHousehold", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "PetHook", "PetOrig");
            Patch(typeof(Household), "DestroyAlienHousehold", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "AlienHook", "AlienOrig");
            Patch(typeof(Household), "DestroyMermaidHousehold", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "MermaidHook", "MermaidOrig");
            Patch(typeof(Household), "DestroyServobotHousehold", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "ServobotHook", "ServobotOrig");
            Patch(typeof(Household), "CreatePreviousTravelerHousehold", Type.EmptyTypes, typeof(SdiHouseStaticsHook), "TravelerHook", "TravelerOrig");
            Patch(typeof(Bin), "PlaceHousehold", new Type[] { typeof(HouseholdContents), typeof(Lot), typeof(Vector3) }, typeof(SdiBinHook), "PlaceHook", "PlaceOrig");
            sSdiReady = Patch(typeof(SimDescription), "Find", new Type[] { typeof(ulong) }, typeof(SdiFindHook), "FindHook", "FindOrig");
            if (!sSdiReady) return;
            // the save's lists arrive without a call: nothing is answered from load start to load finish, or after quit
            World.OnWorldLoadStartedEventHandler += delegate { sSdiHold = true; SdiReset(); };
            World.OnWorldLoadFinishedEventHandler += delegate
            {
                SdiReset(); sSdiHold = false;
                // the mod scan under the load screen when the switch is already on (else at the first lookup with it on)
                try { if (!sSdiScanned && SwOn(kSwSdi)) SdiScan(PatchCounters()); } catch (Exception) { }
            };
            World.OnWorldQuitEventHandler += delegate { sSdiHold = true; SdiReset(); };
        }

        // The game's sim thread, by ts3native (docs/362). Not the sim thread, or not known (older asi): the lookup takes
        // the game's own path and the index stays untouched.
        static bool SdiSimThread() { return OnSimThread(); }

        // a writer's window; on any other thread (no window: the depth is the Sim thread's) the write only marks the snapshot dirty
        static bool SdiOpen() { if (!SdiSimThread()) return false; sSdi.Open(); return true; }
        static void SdiClose(bool opened, bool wrote) { if (opened) sSdi.Close(wrote); else if (wrote) sSdi.Note(); }

        // null when the four required writers and the lookup have the IL that docs/359 §3 was read from; else the first that differs
        static string SdiBodiesRead()
        {
            try
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                Type mem = typeof(Household.Members), sd = typeof(SimDescription);
                MethodBase[] ms = { mem.GetMethod("Add", all, null, new Type[] { typeof(SimDescription), typeof(IActor) }, null), mem.GetMethod("PostLoadFixup", all, null, Type.EmptyTypes, null),
                    sd.GetMethod("MakeUniqueId", all, null, Type.EmptyTypes, null),
                    sd.GetMethod("ImportContent", all, null, new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) }, null),
                    sd.GetMethod("Find", all, null, new Type[] { typeof(ulong) }, null) };
                ulong[] read = { 0xb91418e1bb307ef2UL, 0xd96559adc22c0f0bUL, 0x161725c3d96a1d41UL, 0xa80547dabcda6f52UL, 0xbfb232988c37798cUL };
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i] == null) return "writer " + i + " (missing)";
                    if (AutShadowCore.MethodIlHash(AutShadowCore.SigKey(ms[i]), ms[i], new Dictionary<Module, Dictionary<int, string>>()) != read[i]) return ms[i].DeclaringType.Name + "." + ms[i].Name;
                }
                return null;
            }
            catch (Exception e) { return "hash check (" + e.GetType().Name + ")"; }
        }

        static void SdiReset()
        {
            sSdi.Reset(); sSdiUrns.Clear(); sSdiStaleMisses = 0;
            try { SdiGauges(PatchCounters()); } catch (Exception) { }
        }

        static void SdiGauges(int[] c)
        {
            c[kSdi + 5] = sSdi.Rebuilds; c[kSdi + 6] = sSdi.Entries; c[kSdi + 7] = sSdi.HighWater; c[kSdi + 8] = sSdiUrns.Count;
            c[kSdi + 9] = sSdi.Notes; c[kSdi + 10] = sSdi.Moved; c[kSdi + 19] = sSdiUntrusted; c[kSdi + 22] = sSdiUrns.Seeds;
            c[kSdi + 21] = (sSdiReady ? 1 : 0) | (sSdiUrnOk ? 2 : 0) | (sSdiScanned ? 4 : 0);
        }

        // GameObject.Init (DrInitHook): every urnstone the world gains, whatever the switch (no gap when it is turned on later)
        static void SdiUrnInit(GameObject o)
        {
            if (!sSdiReady) return; // no lookup hook: nothing reads the set and nothing would clear it
            Urnstone u = o as Urnstone;
            if (u == null) return;
            try { sSdiUrns.Add(u.ObjectId.Value, u); } // no id yet: the set is marked not seeded (UrnSet.Add)
            catch (Exception) { sSdiUrns.Seeded = false; PatchCounters()[kSdi + 20]++; }
        }

        // GameObject.Destroy (DrPlDestroyHook), after the game's: drop the reference once the world no longer resolves the id
        static void SdiUrnDestroy(GameObject o, ulong id)
        {
            if (id == 0 || !(o is Urnstone)) return;
            try { if (GameObject.GetObject(new ObjectGuid(id)) == null) sSdiUrns.Drop(id); else sSdiUrns.Stale = true; }
            catch (Exception) { sSdiUrns.Stale = true; }
        }

        // No urn the world may list holds the id (the game's urn loop finds none). False: one does, ask the game.
        static bool SdiNoUrn(ulong id, int[] c)
        {
            UrnSet set = sSdiUrns;
            if (!set.Seeded || (set.Stale && ++sSdiStaleMisses >= 64))
            {
                Urnstone[] all = Sims3.Gameplay.Queries.GetObjects<Urnstone>(); // the game's own query: the set starts as its list
                ulong[] ids = new ulong[all.Length];
                for (int i = 0; i < all.Length; i++) ids[i] = all[i].ObjectId.Value;
                set.Seed(ids, all);
                sSdiStaleMisses = 0;
            }
            bool none = true, stale = false;
            foreach (object o in set.Urns)
            {
                Urnstone u = (Urnstone)o;
                if (u.HasBeenDestroyed) stale = true;
                SimDescription d = u.DeadSimsDescription;
                if (d != null && d.Household == null && d.SimDescriptionId == id) { none = false; break; }
            }
            if (stale) set.Stale = true; // a destroy the hook did not see: re-seed after 64 more misses
            return none;
        }

        // DescIndexCore.Hit / Absent: d is the game's answer. Game: run the game's lookup.
        static int SdiLookup(ulong id, int[] c, out SimDescription d, out int walked)
        {
            d = null; walked = 0;
            if (sSdiHold || GameStates.IsStartingVacation) return DescIndexCore.Game; // the traveler household is walked then
            if (!sSdiScanned) SdiScan(c);
            if (sSdiUntrusted != 0) return DescIndexCore.Game;
            object o;
            int before = sSdi.Rebuilds;
            int r = sSdi.Lookup(id, sSdiWorld, out o, out walked);
            if (sSdi.Rebuilds != before) { SdiGauges(c); DdScanFlush(); }
            if (r == DescIndexCore.Hit) { d = (SimDescription)o; return r; }
            if (r != DescIndexCore.Absent) return r;
            if (!sSdiUrnOk) return DescIndexCore.Game;
            if (!SdiNoUrn(id, c)) { c[kSdi + 4]++; return DescIndexCore.Game; }
            if (IntroTutorial.SimsRemovedFromHousehold != null) return DescIndexCore.Game;
            d = GameStates.GetEarlyDepatureSim(id);
            return DescIndexCore.Absent;
        }

        // Mod writers (docs/359 §5): one scan per process, at the first lookup with the switch on outside a load.
        static void SdiScan(int[] c)
        {
            sSdiScanned = true;
            sSdiUrnOk = sSwapsByHook.ContainsKey(typeof(DrInitHook)); // every Apply has run by the first lookup in a world
            DescWriterScan.Result r = new DescWriterScan.Result();
            int passes = 0, derived = 0, sdiPinned = 0;
            try
            {
                const string hh = "Sims3.Gameplay.CAS.Household", mem = "Sims3.Gameplay.CAS.Household+Members", sd = "Sims3.Gameplay.CAS.SimDescription";
                Dictionary<string, int> fields = new Dictionary<string, int>();
                fields[hh + "::sHouseholdList"] = 1; fields[hh + "::mMembers"] = 1; fields[mem + "::mAllSimDescriptions"] = 1; fields[sd + "::mSimDescriptionId"] = 1;
                Dictionary<string, int> calls = new Dictionary<string, int>();
                calls[hh + "::get_AllSimDescriptions"] = 1; calls[mem + "::get_AllSimDescriptionList"] = 1; calls[sd + "::set_SimDescriptionId"] = 1;
                Dictionary<string, bool> lists = new Dictionary<string, bool>();
                lists[hh + "::sHouseholdList"] = true; lists[mem + "::mAllSimDescriptions"] = true;
                lists[hh + "::get_AllSimDescriptions"] = true; lists[mem + "::get_AllSimDescriptionList"] = true;
                // read by hand: ListSims' local also holds a list it fills itself; only that one is added to [verified]
                Dictionary<string, bool> allowed = new Dictionary<string, bool>();
                allowed["Ts3Agent.Handlers::ListSims"] = true;
                allowed["Ts3Fixes.Fixes::.cctor"] = true; // its "mMembers" is Conversation's (DeadRefs.cs sConvMembers, read only) [verified]
                Assembly own = typeof(Fixes).Assembly;
                DescWriterScan.Core = delegate(Assembly x) { foreach (string g in kGameModules) if (string.Equals(g, x.GetName().Name, StringComparison.OrdinalIgnoreCase)) return true; return false; };
                DescWriterScan.CallTargets = calls; DescWriterScan.AliasLocals = true;
                // read-only shapes (docs/359 §5): EA callees that only read the list they are given [verified decompiled];
                // methods that return a list as it is and mod fields that keep one become targets themselves, so the scan
                // repeats until a pass finds no new one; that pass is the verdict
                DescWriterScan.ReadCallees = new Dictionary<string, bool>();
                DescWriterScan.ReadCallees["Sims3.Gameplay.CAS.MiniSimDescription::UpdateHouseholdMembers"] = true;
                DescWriterScan.ReadCallees["Sims3.Gameplay.Skills.TournamentManagement::FindSuitableOpponent"] = true;
                DescWriterScan.Wrappers = new Dictionary<string, bool>();
                DescWriterScan.Derived = new Dictionary<string, bool>();
                List<Assembly> mods = new List<Assembly>(), rest = new List<Assembly>();
                List<Dictionary<string, bool>> allow = new List<Dictionary<string, bool>>();
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (DescWriterScan.Core(a)) continue; // EA's writers: the hooks and checks above
                    bool refsGameplay = false;
                    foreach (AssemblyName rn in a.GetReferencedAssemblies()) if (rn.Name == "Sims3GameplaySystems") refsGameplay = true;
                    if (!refsGameplay) { rest.Add(a); continue; } // cannot name the EA lists; scanned once a mod getter or field exists
                    Dictionary<string, bool> al = new Dictionary<string, bool>(allowed);
                    // NRaas Register's service picker hands the NPC household's list to its selection base class, which keeps and
                    // walks it (a generic class the scan cannot follow) [read in the NRaas source]; covered while the IL is that one
                    // and the base classes' bodies that touch the kept field are the ones read (DescWriterScan.SdiSelectionRead)
                    if (a != own && DescWriterScan.HashesAre(a, DescWriterScan.SdiSelectionRead))
                        sdiPinned += DescWriterScan.CoverByHash(a, "NRaas.RegisterSpace.Options.Service.SelectService+SimSelection", ".ctor", 0xecd7ec9c49a86425UL, al);
                    mods.Add(a); allow.Add(al);
                }
                for (int pass = 0; pass < 5; pass++)
                {
                    r = new DescWriterScan.Result();
                    DescWriterScan.NewWrappers = 0;
                    // our hook classes are skipped: their stubs hold EA's original bodies (docs/242)
                    for (int i = 0; i < mods.Count; i++)
                        DescWriterScan.ScanAssembly(mods[i], mods[i] == own ? (Predicate<Type>)delegate(Type t) { return sSwapsByHook.ContainsKey(t); } : null,
                            allow[i], DescWriterScan.ReflectionBody, fields, lists, r, 12);
                    passes = pass + 1;
                    if (rest.Count > 0 && DescWriterScan.Wrappers.Count + DescWriterScan.Derived.Count > 0)
                    {   // a mod getter or field can be used without naming an EA type: every non-EA assembly joins, one more pass
                        foreach (Assembly a in rest) { mods.Add(a); allow.Add(allowed); }
                        rest.Clear();
                        if (pass < 4) continue;
                        r.Add(DescWriterScan.KAll, "scan did not settle in 5 passes", 12);
                        break;
                    }
                    if (DescWriterScan.NewWrappers == 0) break;
                    if (pass == 4) r.Add(DescWriterScan.KAll, "scan did not settle in 5 passes", 12);
                }
                derived = DescWriterScan.Wrappers.Count + DescWriterScan.Derived.Count;
            }
            catch (Exception ex) { r.Unreadable++; r.Add(DescWriterScan.KAll, "scan failed: " + ex.GetType().Name, 12); }
            finally
            {
                DescWriterScan.CallTargets = null; DescWriterScan.AliasLocals = false;
                DescWriterScan.ReadCallees = null; DescWriterScan.Wrappers = null; DescWriterScan.Derived = null;
            }
            sSdiUntrusted = r.Mask == 0 ? 0 : Math.Max(1, r.PerKind[0]);
            DdScanLine("simdescindex scan: " + r.Assemblies + " assemblies, " + r.Methods + " methods, " + r.Unreadable + " unreadable, " + r.Unresolved
                + " unresolvable-token, " + r.Reflect + " reflection, " + passes + " passes, " + derived + " list getters / fields followed, " + sdiPinned + " covered by hash; " + (r.Mask == 0 ? "trusted" : "UNTRUSTED, lookups stay the game's this session"));
            foreach (string l in r.Log)
            {
                int w = l.IndexOf(" writes ");
                DdScanLine("simdescindex scan: writer " + (w > 0 ? l.Substring(0, w) : l) + (l.IndexOf(" at ") > w && w > 0 ? l.Substring(l.IndexOf(" at ", w)) : ""));
            }
            DdScanFlush();
            SdiGauges(c);
        }

        public static class SdiFindHook
        {
            public static SimDescription FindHook(ulong id)
            {
                if (!sSdiReady || !SwOn(kSwSdi) || !SdiSimThread()) return FindOrig(id);
                int[] c = PatchCounters();
                c[kSdi]++;
                SimDescription ours = null;
                int walked = 0, r = DescIndexCore.Game;
                try { r = SdiLookup(id, c, out ours, out walked); }
                catch (Exception) { c[kSdi + 20]++; sSdi.Drop(); sSdiUrns.Clear(); r = DescIndexCore.Game; } // Drop keeps an open writer window
                if (r == DescIndexCore.Game) { c[kSdi + 3]++; return FindOrig(id); }
                bool hit = r == DescIndexCore.Hit;
                if (hit) c[kSdi + 1]++; else { c[kSdi + 2]++; c[kSdi + 12]++; }
                sSdiWalked += walked;
                c[kSdi + 11] = (int)Math.Min(sSdiWalked / 1000, int.MaxValue);
                if ((++sSdiAnswered & 63) != 0 && !SwOn(kSwSdiVerify)) return ours;
                // shadow: the game's walk, timed; its answer is the one returned
                if (sSdiWatch == null) sSdiWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                sSdiWatch.Restart();
                SimDescription game = FindOrig(id);
                int us = (int)Math.Min(sSdiWatch.GetElapsedTime(), 1000000);
                c[kSdi + 13]++;
                if (hit) { c[kSdi + 15] += us; c[kSdi + 16] += walked; } else { c[kSdi + 17] += us; c[kSdi + 18]++; }
                if (!ReferenceEquals(game, ours))
                {
                    c[kSdi + 14]++;
                    sSdi.Note();
                    if (sSdiUntrusted == 0) sSdiUntrusted = 1; // one wrong answer: every lookup is the game's for the rest of the session
                    if (sSdiLogged++ < 5) DdScanLine("simdescindex MISMATCH id " + id.ToString("x16") + ": index " + (hit ? "hit" : ours != null ? "early-departure" : "absent")
                        + ", game " + (game == null ? "null" : "found" + (game.Household == null ? " (no household)" : "")));
                }
                SdiGauges(c);
                return game;
            }
            public static SimDescription FindOrig(ulong id) { return null; } // receives SimDescription.Find's original header
        }

        // this = Household.Members
        public class SdiMembersHook
        {
            public bool AddHook(SimDescription simDescription, IActor actor)
            {
                Household.Members m = (Household.Members)(object)this;
                int n = m.Count;
                bool w = SdiOpen();
                try { return AddOrig(simDescription, actor); }
                finally { SdiClose(w, m.Count != n); }
            }
            public bool AddOrig(SimDescription simDescription, IActor actor) { return false; } // Members.Add

            public void FixupHook()
            {
                Household.Members m = (Household.Members)(object)this;
                int n = m.Count;
                bool w = SdiOpen();
                try { FixupOrig(); }
                finally { SdiClose(w, m.Count != n); }
            }
            public void FixupOrig() { } // Members.PostLoadFixup

            public bool RemoveAtHook(int index) { try { return RemoveAtOrig(index); } finally { sSdi.Note(); } }
            public bool RemoveAtOrig(int index) { return false; } // Members.RemoveAt (Members.Remove calls it)

            public void DisposeEachHook() { try { DisposeEachOrig(); } finally { sSdi.Note(); } }
            public void DisposeEachOrig() { } // Members.DisposeEachMember
        }

        // this = SimDescription: the two bodies that store the id of a description that may sit in a household
        public class SdiDescHook
        {
            public ulong UniqueHook()
            {
                SimDescription d = (SimDescription)(object)this;
                ulong id = d.SimDescriptionId;
                bool w = SdiOpen();
                try { return UniqueOrig(); }
                finally { SdiClose(w, d.SimDescriptionId != id); }
            }
            public ulong UniqueOrig() { return 0; } // SimDescription.MakeUniqueId

            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                SimDescription d = (SimDescription)(object)this;
                ulong id = d.SimDescriptionId;
                bool w = SdiOpen();
                try { return ImportOrig(resKeyTable, objIdTable, reader); }
                finally { SdiClose(w, d.SimDescriptionId != id); }
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; } // SimDescription.ImportContent
        }

        // this = Household
        public class SdiHouseHook
        {
            public void ClearHook() { try { ClearOrig(); } finally { sSdi.Note(); } }
            public void ClearOrig() { } // Household.ClearSilent
        }

        public static class SdiHouseStaticsHook
        {
            public static void CleanupHook() { try { CleanupOrig(); } finally { sSdi.Note(); } }
            public static void CleanupOrig() { } // Household.Cleanup
            public static void StartupHook() { try { StartupOrig(); } finally { sSdi.Note(); } }
            public static void StartupOrig() { } // Household.Startup
            public static void ShutdownHook() { try { ShutdownOrig(); } finally { sSdi.Note(); } }
            public static void ShutdownOrig() { } // Household.Shutdown
            public static void PetHook() { try { PetOrig(); } finally { sSdi.Note(); } }
            public static void PetOrig() { } // Household.DestroyPetHousehold
            public static void AlienHook() { try { AlienOrig(); } finally { sSdi.Note(); } }
            public static void AlienOrig() { } // Household.DestroyAlienHousehold
            public static void MermaidHook() { try { MermaidOrig(); } finally { sSdi.Note(); } }
            public static void MermaidOrig() { } // Household.DestroyMermaidHousehold
            public static void ServobotHook() { try { ServobotOrig(); } finally { sSdi.Note(); } }
            public static void ServobotOrig() { } // Household.DestroyServobotHousehold
            public static void TravelerHook() { try { TravelerOrig(); } finally { sSdi.Note(); } }
            public static void TravelerOrig() { } // Household.CreatePreviousTravelerHousehold
        }

        // this = Bin: a placed household can join the list with its members already in it
        public class SdiBinHook
        {
            public bool PlaceHook(HouseholdContents contents, Lot lot, Vector3 pos) { bool w = SdiOpen(); try { return PlaceOrig(contents, lot, pos); } finally { SdiClose(w, true); } }
            public bool PlaceOrig(HouseholdContents contents, Lot lot, Vector3 pos) { return false; } // Bin.PlaceHousehold
        }
    }
}
