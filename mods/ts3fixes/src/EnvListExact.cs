using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // ts3native 0.36.284+ (registered with the [UnseenTable] ICs): the print of the id sequence the game's lot walk would
    // push for the lot right now (lot vector in order, then the world-lot objects whose component names the lot); 0 = no read
    public static class EnvOrderNative
    {
        [MethodImpl(MethodImplOptions.InternalCall)] public static extern ulong LotPrint(ulong lot);
    }

    // docs/274 §11 envlistmemoexact (test switch, default off): the env scan's two room lists (GetObjectsInRoom<GameObject>,
    // GetObjectsInRoom<IAromainator>) from the env list memo, for every Sim, inside the game's own
    // SimUpdate.CalculateSimEnvironmentScore (no replica: scores, events and reactions are the game's code in its order).
    // How the list gets in: Lot.GetObjectsInRoom<T> first asks the lot's own per-type cache table (EA's ObjectCache
    // mechanism, used by EA for IPortalConnectionObject only). For the head of one scan we put our cache under the two
    // types; it serves the memo list to the scan's own call (same room, once), takes itself out, and answers anything
    // else by EA's own code. A miss lets EA's code list and keeps a copy (no double listing).
    // When a memo list is the game's list (all must hold, else the game lists itself):
    //   - the lot's native order print equals the print taken before the list was built (EnvOrderNative.LotPrint: the same
    //     reads as the game's walker, so every native writer of the order is covered without hooking each one);
    //   - no managed lot / room / destroy / place event for the room or lot since (ListMemo generations, docs/274 §8.1),
    //     no object of the lot pending destruction (§8.5);
    //   - nothing runs between our check and the scan's list calls but reads: scans with a puddle or burnt tile (an event
    //     is sent first) and lots of another class than Lot take the game's call.
    // Household, selected and unknown-scope Sims are included because the list is the game's own (Rodney 2026-11-18: "Turn it
    // on for household Sims"): no per-reuse verify for them; the sampled verify (every 4th reuse) stays, and the first
    // difference anywhere sends them back to the game's call for the session (and turns the lot's memo off for everyone).
    // Threads: the lot's cache table is only read in the game; we write it. So nothing happens while the ts3native script
    // worker has a job (ExWorkerBusy; jobs are dispatched by the sim thread between ticks, never inside a scan).
    // Stand-down (docs/274 §11.8): a known conflicting mod assembly, another mod's swap of CalculateSimEnvironmentScore
    // (before or after ours) or another mod's cache object in a lot's cache table turns the path off for the session with
    // one "EnvListMemo.Exact: stand down: <mod> (<what>)" line; counter 1262.
    // Counters 1241-1254, 1261, 1262 (envlistexact_*). envscancost (test switch): env scan calls and us by scope, counters 1255-1260.
    public static partial class Fixes
    {
        const int cEx = 1241, cEc = 1255, cExWorker = 1261, cExStand = 1262, kExLog = 20;
        static FieldInfo fExCaches;
        static ElCache sExG, sExA;
        static bool sExOff, sExDiffSeen, sExInstalled, sExIcTried;
        static int sExRoom, sExServed, sExCaptured, sExVerifyN, sExLogged, sExWk;  // sExWk: 0 untried, 1 a script worker mode is on, -1 none
        static ulong sExSig;
        static long sExTick;
        static readonly Dictionary<ulong, bool> sExLotOff = new Dictionary<ulong, bool>();
        static StopWatch sEcWatch;
        static MethodInfo sExEnvM;  // SimUpdate.CalculateSimEnvironmentScore (ExNoteEnvSwap, before our swap)
        static string sExPreOwner;  // assembly that owned its header before our swap; null = EA's
        static bool sExModsChecked;

        sealed class ElCache : Lot.IObjectCache
        {
            public Type Key;
            public int Kind;          // the memo's list number: 0 room GameObjects, 2 room aromainators
            public Lot Owner;
            public IDictionary Table; // Owner's cache table while this cache is in it, else null
            public object[] Armed;    // the memo list to serve; null = keep the game's list

            public int Version { get { return 0; } }
            public void Refresh() { ExForeign(); }
            public void RefreshUsers() { ExForeign(); }

            public void Remove()
            {
                if (Table != null) Table.Remove(Key);
                Table = null;
            }

            public List<T> GetObjectsInRoom<T>(int roomId) where T : class, IGameObject
            {
                Lot lot = Owner;
                object[] memo = Armed;
                bool mine = Table != null && sExInstalled && roomId == sExRoom;
                ulong sig = sExSig;
                long tick = sExTick;
                Remove();  // first, so the live call below is EA's own path
                Armed = null;
                if (lot == null) return new List<T>();  // never in a table without an owner
                if (mine && memo != null)
                {
                    try
                    {
                        List<T> l = new List<T>(memo.Length + 4);
                        for (int i = 0; i < memo.Length; i++) l.Add((T)memo[i]);
                        sExServed++;
                        return l;
                    }
                    catch (Exception) { PatchCounters()[cEx + 7]++; }  // the game lists below
                }
                List<T> live = lot.GetObjectsInRoom<T>(roomId);
                if (mine) { sEl.Put(lot, roomId, Kind, live, tick, sig); sExCaptured++; }
                else PatchCounters()[cEx + 12]++;
                return live;
            }
        }

        // the lot's cache table was refreshed with our cache in it (not expected: only reads run in the scan head): out for the session
        static void ExForeign()
        {
            if (sExOff) return;
            sExOff = true;
            PatchCounters()[cEx + 13]++;
            AppliedLog().Add("EnvListMemo.Exact: off for the session: the lot's cache table was refreshed while our cache was in it");
        }

        static void ExUninstall()
        {
            if (sExG != null) { sExG.Remove(); sExG.Armed = null; sExG.Owner = null; }
            if (sExA != null) { sExA.Remove(); sExA.Armed = null; sExA.Owner = null; }
            sExInstalled = false;
        }

        // ApplyAutOffload, just before our swap of SimUpdate.CalculateSimEnvironmentScore: another mod's swap already in place?
        // (MethodBase.DeclaringType reads the header's class, so it names that mod's class.)
        static void ExNoteEnvSwap(MethodInfo m)
        {
            sExEnvM = m;
            try { if (m != null && !L3Ok(m)) sExPreOwner = m.DeclaringType.Assembly.GetName().Name; } catch (Exception) { }
        }

        // true = stand down (sExOff set, logged and counted once). Loaded assemblies are read at the first scan only (mods
        // load at start-up); the header and the lot's table at every scan (a pointer read and a 1-3 entry table).
        // shortcut: Lot.GetObjectsInRoom<T> is generic, so a swap of it is not checked (L3Ok passes generic code); add an
        // inflated-method check if a mod is ever found that swaps it.
        static bool ExStandDown(IDictionary table)
        {
            string[] loaded = null;
            if (!sExModsChecked)
            {
                sExModsChecked = true;
                List<string> n = new List<string>();
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) n.Add(a.GetName().Name);
                loaded = n.ToArray();
            }
            string now = sExEnvM == null || OwnSwap(sExEnvM) || L3Ok(sExEnvM) ? null : sExEnvM.DeclaringType.Assembly.GetName().Name;
            string[] caches = new string[table.Count];
            int i = 0;
            foreach (object v in table.Values) caches[i++] = v == null ? EnvListExactCore.Ea : v.GetType().Assembly.GetName().Name;
            string why = EnvListExactCore.Why(EnvListExactCore.ConflictMods, loaded, sExPreOwner, now, caches);
            if (why == null) return false;
            sExOff = true;
            PatchCounters()[cExStand]++;
            AppliedLog().Add("EnvListMemo.Exact: stand down: " + why);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)] static ulong ExPrintIc(ulong lot) { return EnvOrderNative.LotPrint(lot); }
        [MethodImpl(MethodImplOptions.NoInlining)] static int ExShadowCfg(int key) { return AutShadowNative.Config(key); }

        // true = the ts3native script worker may be running managed code now (AutShadow Mode >= 1 and the worker not idle,
        // or not up yet). No AutShadow ICs = no worker.
        static bool ExWorkerBusy()
        {
            if (sExWk < 0) return false;
            try
            {
                if (sExWk == 0) sExWk = ExShadowCfg(0) >= 1 ? 1 : -1;
                return sExWk > 0 && ExShadowCfg(4) == 0;
            }
            catch (Exception) { sExWk = -1; return false; }
        }

        static ulong ExPrint(ulong lot)
        {
            try { ulong p = ExPrintIc(lot); sExIcTried = true; return p; }
            catch (Exception e)
            {
                if (!sExIcTried)
                {
                    sExIcTried = true; sExOff = true;
                    AppliedLog().Add("EnvListMemo.Exact: off: no EnvOrderNative.LotPrint (needs ts3native 0.36.284+ with [UnseenTable] Enabled=1): " + e.GetType().Name);
                }
                return 0;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ExMakeCaches()
        {
            ElCache g = new ElCache(), a = new ElCache();
            g.Key = typeof(GameObject); g.Kind = 0;
            a.Key = typeof(IAromainator); a.Kind = 2;
            sExG = g; sExA = a;
        }

        // a type load failure of ElCache (it implements a private EA interface) lands here, not in the scan
        static bool ExMake()
        {
            try
            {
                fExCaches = typeof(Lot).GetField("mObjectCaches", BindingFlags.Instance | BindingFlags.NonPublic);
                if (fExCaches == null) throw new MissingFieldException("Lot.mObjectCaches");
                ExMakeCaches();
                return true;
            }
            catch (Exception e)
            {
                sExOff = true; sExG = sExA = null;
                AppliedLog().Add("EnvListMemo.Exact: off: " + e.GetType().Name + " " + e.Message);
                return false;
            }
        }

        static bool ExVerify(Lot lot, int room, int kind, object[] memo, IList live)
        {
            if (ListMemo.Same(memo, live)) return true;
            if (sExLogged < kExLog)
            {
                sExLogged++;
                sEl.WrongEv = 0; sEl.WrongAge = 0;  // ElDiff prints them; the exact path keeps no event count
                AppliedLog().Add("EnvListMemo: wrong hit exact lot " + lot.LotId + " room " + room + " list " + kind + " n " + live.Count + ElDiff(memo, live) + " (lot off for the session)");
            }
            return false;
        }

        // true = our cache is in the lot's table for the scan head; the caller runs the game's method, then EnvListExactEnd
        static bool EnvListExactBegin(SimUpdate u)
        {
            int[] c = PatchCounters();
            try
            {
                if (sExOff || sExInstalled) return false;  // a re-entered scan (no such path known) takes the game's call
                c[cEx]++;
                Sim s = fUpdSim.GetValue(u) as Sim;
                if (s == null) { c[cEx + 3]++; return false; }
                if (sExDiffSeen)  // household / selected / unknown scope: the game's call once any verify difference was seen
                {
                    int sc = AutOffloadCore.Scope(SafeUfWhy(s));
                    if (IsPlayerSim(s) || sc == AutOffloadCore.kScopeHousehold || sc == AutOffloadCore.kScopeUnknown) { c[cEx + 10]++; return false; }
                }
                Lot lot = s.LotCurrent;
                if (lot == null || s.RabbitHoleEnvironmentScore != float.MinValue || s.IsOutside) { c[cEx + 3]++; return false; }
                int room = s.RoomId;
                EnvironmentRoomInfo info = Sims3.SimIFace.World.GetInformationAboutRoom(lot.LotId, room);
                if (info.roomData.sizeInTileQuadrants == 0) { c[cEx + 3]++; return false; }
                if (info.roomData.puddleCount != 0 || info.roomData.burntTileCount != 0) { c[cEx + 4]++; return false; }  // the game sends an event before it lists
                if (lot.GetType() != typeof(Lot) || sExLotOff.ContainsKey(lot.LotId)) { c[cEx + 5]++; return false; }
                if (!ElPrep()) { c[cEx + 7]++; return false; }
                if (sExG == null && !ExMake()) { c[cEx + 7]++; return false; }
                IDictionary d = fExCaches.GetValue(lot) as IDictionary;
                if (d == null) { c[cEx + 5]++; return false; }
                if (ExStandDown(d)) return false;
                if (d.Contains(sExG.Key) || d.Contains(sExA.Key)) { c[cEx + 5]++; return false; }  // EA's own cache of these types (none known): theirs
                if (ExWorkerBusy()) { c[cExWorker]++; return false; }
                ulong sig = ExPrint(lot.LotId);
                if (sig == 0) { c[cEx + 6]++; return false; }
                long tick = Simulator.TicksElapsed();
                object[] m0 = sEl.GetExact(lot, room, 0, tick, sig), m2 = sEl.GetExact(lot, room, 2, tick, sig);
                if (m0 != null && m2 != null && (++sExVerifyN & 3) == 0)  // always with the exact path: household Sims rely on it (docs/274 §11.4)
                {
                    c[cEx + 8]++;
                    bool ok0 = ExVerify(lot, room, 0, m0, lot.GetObjectsInRoom<GameObject>(room));
                    bool ok2 = ExVerify(lot, room, 2, m2, lot.GetObjectsInRoom<IAromainator>(room));
                    if (!ok0 || !ok2)
                    {
                        c[cEx + 9]++; c[cEx + 11]++;
                        sExDiffSeen = true;
                        sExLotOff[lot.LotId] = true;
                        return false;
                    }
                }
                sExRoom = room; sExSig = sig; sExTick = tick; sExServed = sExCaptured = 0;
                sExG.Owner = sExA.Owner = lot;
                sExG.Armed = m0; sExA.Armed = m2;
                d[sExG.Key] = sExG; sExG.Table = d;
                d[sExA.Key] = sExA; sExA.Table = d;
                sExInstalled = true;
                return true;
            }
            catch (Exception) { c[cEx + 7]++; ExUninstall(); return false; }
        }

        static void EnvListExactEnd()
        {
            int[] c = PatchCounters();
            if (sExServed == 2) c[cEx + 1]++;
            else if (sExServed + sExCaptured == 2) c[cEx + 2]++;
            else c[cEx + 12]++;  // the scan did not make both list calls with our cache in place (an exception, or another caller came first)
            ExUninstall();
        }

        // an object is about to change lot or start up while our cache is in a lot's table (not expected: only reads run in
        // the scan head): out before EA's Lot.RefreshObjectCachesIfNecessary can see our keys
        static void ExOnLotEvent()
        {
            if (!sExInstalled) return;
            PatchCounters()[cEx + 12]++;
            ExUninstall();
        }

        // ---- envscancost (docs/274 §11, measure before build): calls and us of the whole env scan hook by scope ----
        static long EnvCostStart()
        {
            if (sEcWatch == null) sEcWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sEcWatch.Restart();  // the env scan hook does not nest
            return 0;
        }

        static void EnvCostEnd(SimUpdate u, long t0)
        {
            try
            {
                long us = sEcWatch.GetElapsedTime() - t0;
                Sim s = fUpdSim.GetValue(u) as Sim;
                int sc = AutOffloadCore.Scope(SafeUfWhy(s));
                int k = IsPlayerSim(s) || sc == AutOffloadCore.kScopeHousehold || sc == AutOffloadCore.kScopeUnknown ? 0 : sc == AutOffloadCore.kScopeSeen ? 1 : 2;
                int[] c = PatchCounters();
                c[cEc + k]++;
                c[cEc + 3 + k] = us >= int.MaxValue - c[cEc + 3 + k] ? int.MaxValue : c[cEc + 3 + k] + (int)us;  // saturates (~36 min of scan time)
            }
            catch (Exception) { }
        }
    }
}
