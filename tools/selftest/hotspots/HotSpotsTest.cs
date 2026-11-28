// Usage: node tools/selftest/hotspots/run.ts  (compiles this with mods/ts3fixes/src/TickMemo.cs)
// Offline self-check of TickMemo.cs: the per-tick stamp rules shared by the docs/74 memos, the reference set behind
// the detail-priority lot set, the hours-of-operation memo (outputs written only on a true answer) and the tooltip
// poll gate, and the docs/110 wildflower fire bookkeeping (WildflowerFire), each checked against fixed cases or a plain model on random call sequences. Exit code 1 on failure.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class HotSpotsTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    static void StampRules()
    {
        TickStamp s = new TickStamp();
        object a = new object(), b = new object();
        Check(s.Renew(5, a, 1000), "first use is stale");
        Check(!s.Renew(5, a, 1050), "same tick, owner, 50 ms: fresh");
        Check(!s.Renew(5, a, 1100), "100 ms: still fresh");
        Check(s.Renew(5, a, 1101), "101 ms: stale (time cap)");
        Check(s.Renew(6, a, 1102), "next tick: stale");
        Check(s.Renew(6, b, 1103), "owner changed: stale");
        Check(!s.Renew(6, b, 1104), "same again: fresh");
        Check(s.Renew(6, b, 1000), "clock went back: stale");
        s.Reset();
        Check(s.Renew(6, b, 1001), "after Reset: stale");
        Check(s.Renew(6, null, 1002), "owner null after b: stale");
        Check(!s.Renew(6, null, 1003), "owner null twice: fresh");
        // TickCount wraps after ~25 days: the age is taken modulo 2^32
        Check(s.Renew(7, null, int.MaxValue - 10), "wrap: new tick");
        Check(!s.Renew(7, null, unchecked(int.MaxValue + 50)), "wrap: 60 ms later is fresh");
    }

    static void RefSetModel()
    {
        Random r = new Random(74);
        object[] pool = new object[40];
        for (int i = 0; i < pool.Length; i++) pool[i] = i % 3 == 0 ? (object)("lot" + (i % 6)) : new object(); // equal text, distinct refs
        RefSet set = new RefSet();
        List<object> model = new List<object>();
        for (int step = 0; step < 20000; step++)
        {
            int op = r.Next(10);
            if (op == 0) { set.Clear(); model.Clear(); }
            else if (op < 5)
            {
                object o = r.Next(20) == 0 ? null : pool[r.Next(pool.Length)];
                set.Add(o);
                if (o != null && !model.Exists(x => ReferenceEquals(x, o))) model.Add(o);
            }
            else
            {
                object o = pool[r.Next(pool.Length)];
                Check(set.Contains(o) == model.Exists(x => ReferenceEquals(x, o)), "RefSet.Contains at step " + step);
            }
            Check(set.Count == model.Count, "RefSet.Count at step " + step);
            if (sFails > 10) return;
        }
    }

    // Model of Lot.GetHoursOfOperation (docs/276 §1.3): the answer depends on the lot's venue key, on whether that key is a
    // bar (bar hours) and, on a bar, on a started bachelor party (closing hour = party end). Keys and parties change mid-tick.
    const float kPartyEnd = 6f;
    static int[] sKey = new int[30];
    static bool[] sParty = new bool[30];
    static bool IsBar(int key) { return key % 3 == 0; }
    static bool Bar(int key, ref float open, ref float close) { if (!IsBar(key)) return false; open = key % 24; close = (key * 5) % 24 + 0.5f; return true; }
    static bool Game(int lot, ref float open, ref float close)
    {
        int key = sKey[lot];
        if (Bar(key, ref open, ref close)) { if (sParty[lot]) close = kPartyEnd; return true; }
        if (key % 4 == 1) return false;
        open = (key * 7) % 24; close = (key * 11) % 24 + 0.25f;
        return true;
    }

    // The hook's logic around HoursMemo, without the game: same stamp, key, live party check, hit and miss paths.
    static bool Memo(HoursMemo m, int lot, long tick, int nowMs, ref float open, ref float close, out bool hit)
    {
        if (m.Stamp.Renew(tick, null, nowMs)) m.Clear();
        ulong id = (ulong)lot + 0x1000000000UL;
        float o = open, c = close;
        bool res, bar;
        int got = m.TryGet(id, sKey[lot], ref o, ref c, out res, out bar);
        hit = got == HoursMemo.kHit;
        if (hit)
        {
            if (bar && res && sParty[lot]) c = kPartyEnd;
            if (res) { open = o; close = c; }
            return res;
        }
        res = Game(lot, ref open, ref close);
        float bo = 0f, bc = 0f;
        bool barOk = Bar(sKey[lot], ref bo, ref bc);
        m.Store(id, sKey[lot], res, open, close, barOk, bo, bc, kPartyEnd);
        return res;
    }

    static void HoursModel()
    {
        Random r = new Random(87);
        HoursMemo m = new HoursMemo();
        for (int i = 0; i < sKey.Length; i++) sKey[i] = r.Next(40);
        long tick = 0; int now = 0, hits = 0, barHits = 0;
        for (int step = 0; step < 50000; step++)
        {
            if (r.Next(12) == 0) { tick++; now += 25; }
            if (r.Next(500) == 0) now += 150; // ticks stopped: the time cap must rebuild
            int lot = r.Next(30);
            if (r.Next(200) == 0) sKey[lot] = r.Next(40);        // venue changed mid-tick
            if (r.Next(50) == 0) sParty[lot] = !sParty[lot];     // a bachelor party starts or ends mid-tick
            float inOpen = r.Next(3) == 0 ? -1f : (float)r.NextDouble(), inClose = (float)r.NextDouble() * 3f; // caller's values
            float go = inOpen, gc = inClose, mo = inOpen, mc = inClose;
            bool hit;
            bool g = Game(lot, ref go, ref gc);
            bool a = Memo(m, lot, tick, now, ref mo, ref mc, out hit);
            if (hit) { hits++; if (IsBar(sKey[lot])) barHits++; }
            Check(g == a && go == mo && gc == mc, "hours memo at step " + step + " lot " + lot + " tick " + tick);
            if (sFails > 10) return;
        }
        Check(hits > 10000, "hours memo hits " + hits);
        Check(barHits > 1000, "hours memo bar hits " + barHits);
        // a foreign answer on a bar (not the bar formula) is not kept: the next ask is a miss again
        HoursMemo f = new HoursMemo();
        float fo = 0f, fc = 0f; bool fr, fb;
        Check(!f.Store(1UL, 3, true, 9f, 10f, true, 8f, 10f, kPartyEnd), "foreign bar answer refused");
        Check(f.TryGet(1UL, 3, ref fo, ref fc, out fr, out fb) == HoursMemo.kMiss, "refused answer is a miss");
        Check(f.Store(1UL, 3, true, 8f, kPartyEnd, true, 8f, 10f, kPartyEnd), "bar answer with the party end kept");
        Check(f.TryGet(1UL, 3, ref fo, ref fc, out fr, out fb) == HoursMemo.kHit && fb && fc == 10f, "bar entry keeps the bar's own close");
        Check(f.TryGet(1UL, 4, ref fo, ref fc, out fr, out fb) == HoursMemo.kKeyChanged, "other key = key-changed miss");
    }

    static void GateRules()
    {
        PollGate g = new PollGate();
        Check(!g.Skip(10, 20, 0), "never ran: walk");
        g.Ran(10, 20, 1000);
        Check(g.Skip(10, 20, 1000), "same point, 0 ms: skip");
        Check(g.Skip(10, 20, 1099), "same point, 99 ms: skip");
        Check(!g.Skip(10, 20, 1100), "same point, 100 ms: walk");
        Check(!g.Skip(10.5f, 20, 1050), "cursor moved: walk");
        Check(!g.Skip(10, 20, 900), "clock went back: walk");
        g.Ran(10, 20, int.MaxValue - 20);
        Check(g.Skip(10, 20, unchecked(int.MaxValue + 30)), "wrap: 50 ms later skips");
        // a resting cursor polled every 25 ms (a ~40 ticks/s loop) walks about every 100 ms
        g = new PollGate();
        int walks = 0;
        for (int t = 0; t < 10000; t += 25) if (!g.Skip(5, 5, t)) { walks++; g.Ran(5, 5, t); }
        Check(walks >= 99 && walks <= 101, "resting cursor walks " + walks + " in 10 s");
    }

    // docs/110 §7.4: the hook's loop over WildflowerFire against the game's placement loop (stop at the first placed try,
    // give up after MaxTries) on stub placements: same tries run, same result, same counter deltas.
    static void WildflowerModel()
    {
        Random r = new Random(110);
        int[] c = new int[99];
        int fires = 0, tries = 0, placed = 0, failed = 0;
        for (int fire = 0; fire < 5000 && sFails < 10; fire++)
        {
            int max = r.Next(6) == 0 ? r.Next(3) : 20; // 0 and 1 tries too
            bool[] stub = new bool[max];
            int hitAt = r.Next(3) == 0 ? -1 : r.Next(25); // -1 or >= max: every try fails
            for (int i = 0; i < max; i++) stub[i] = i == hitAt;
            // the game's loop
            int gameTries = 0; bool gamePlaced = false;
            for (int i = 0; i < max; i++) { gameTries++; if (stub[i]) { gamePlaced = true; break; } }
            // the hook's loop
            WildflowerFire f = new WildflowerFire(max);
            int n = 0;
            for (bool done = f.MaxTries <= 0; !done; ) { done = f.Try(stub[n], 1000 * (n + 1)); n++; }
            Check(n == gameTries && f.Tries == gameTries, "wildflower tries, fire " + fire);
            Check(f.Placed == gamePlaced, "wildflower result, fire " + fire);
            f.Count(c, true);
            fires++; tries += gameTries; if (gamePlaced) placed++; else failed++;
            Check(c[93] == fires && c[94] == tries && c[95] == placed && c[96] == failed, "wildflower counters, fire " + fire);
        }
        Check(WildflowerFire.MustStop(true, false) && WildflowerFire.MustStop(false, true) && !WildflowerFire.MustStop(false, false), "wildflower: a save or travel ends the fire between tries");
        WildflowerFire g = new WildflowerFire(20);
        g.Try(false, 30000); g.Try(false, 45000); g.Try(true, 5000);
        int[] d = new int[99];
        g.Count(d, true); Check(d[97] == 80, "wildflower [97] whole fire = 80 ms");
        d[97] = 0; g.Count(d, false); Check(d[97] == 45, "wildflower [97] task = longest try 45 ms");
    }

    // ---- lane L4 1115 exact memos: every hook's logic against a model of the game's state, answer equal at every call.

    sealed class FakeBuff { public bool Perma; }
    sealed class FakeInst { public FakeBuff Buff; }

    // permamemo: random adds (Count moves), parse overwrites (parse generation), mBuff swaps and flag-preserving lookups.
    static void PermaModel()
    {
        Random r = new Random(245);
        Dictionary<ulong, FakeInst> table = new Dictionary<ulong, FakeInst>();
        PermaMemo m = new PermaMemo();
        int parseGen = 0, hits = 0;
        for (int step = 0; step < 60000 && sFails < 10; step++)
        {
            int op = r.Next(1000);
            ulong key = (ulong)r.Next(200) * 0x10001UL; // 200 names into 256 slots: collisions and evictions
            if (op < 5) { FakeInst i = new FakeInst(); i.Buff = new FakeBuff(); i.Buff.Perma = r.Next(2) == 0; if (!table.ContainsKey(key)) table.Add(key, i); }
            else if (op < 8) { parseGen++; FakeInst i = new FakeInst(); i.Buff = new FakeBuff(); i.Buff.Perma = r.Next(2) == 0; table[key] = i; } // ParseBuffData overwrite
            else if (op < 12) { FakeInst i; if (table.TryGetValue(key, out i)) { i.Buff = new FakeBuff(); i.Buff.Perma = r.Next(2) == 0; } } // a mod swaps mBuff
            else
            {
                FakeInst gi;
                bool game = key != 0 && table.TryGetValue(key, out gi) && gi.Buff.Perma;
                // the hook
                m.Stamp(table.Count, parseGen);
                object inst, buff; bool ans; bool got;
                if (m.TryGet(key, out inst, out buff, out ans) && (inst == null || ReferenceEquals(((FakeInst)inst).Buff, buff))) { got = ans; hits++; }
                else
                {
                    FakeInst fi;
                    bool found = key != 0 && table.TryGetValue(key, out fi);
                    fi = found ? table[key] : null;
                    got = found && fi.Buff.Perma;
                    m.Store(key, fi, found ? fi.Buff : null, got);
                }
                Check(got == game, "permamemo at step " + step + " key " + key);
            }
        }
        Check(hits > 20000, "permamemo hits " + hits);
        Check(!m.Stamp(table.Count, parseGen) && m.Clear(), "permamemo: same stamp keeps, Clear drops");
    }

    // buffremovenoop: vanilla RemoveElement(name) on random states; whenever the rule says no-op the state is unchanged.
    static void BuffRemoveModel()
    {
        Random r = new Random(95);
        int noops = 0;
        for (int step = 0; step < 40000 && sFails < 10; step++)
        {
            int name = r.Next(6);
            HashSet<int> values = new HashSet<int>(); for (int i = 0; i < 4; i++) if (r.Next(3) == 0) values.Add(r.Next(6));
            Dictionary<int, int> counts = new Dictionary<int, int>(); for (int i = 0; i < 2; i++) if (r.Next(4) == 0) counts[r.Next(6)] = 1;
            List<int> delayed = r.Next(4) == 0 ? null : new List<int>(); if (delayed != null) for (int i = 0; i < 2; i++) if (r.Next(3) == 0) delayed.Add(r.Next(6));
            string before = string.Join(",", new List<int>(values).ConvertAll(x => x.ToString()).ToArray()) + "|" + counts.Count + "|" + (delayed == null ? -1 : delayed.Count);
            // vanilla: RemoveElement((ulong)guid, clearRefCounts: true)
            if (counts.ContainsKey(name)) counts.Remove(name);
            bool done = false;
            if (delayed != null) { int at = delayed.IndexOf(name); if (at >= 0) { delayed.RemoveAt(at); done = true; } }
            if (!done) values.Remove(name);
            string after = string.Join(",", new List<int>(values).ConvertAll(x => x.ToString()).ToArray()) + "|" + counts.Count + "|" + (delayed == null ? -1 : delayed.Count);
            bool wasActive = before.Split('|')[0].Split(',').Length > 0 && Array.IndexOf(before.Split('|')[0].Split(','), name.ToString()) >= 0;
            int cnt0 = int.Parse(before.Split('|')[1]), del0 = int.Parse(before.Split('|')[2]);
            if (BuffRemoveRule.IsNoOp(wasActive, false, cnt0, del0 < 0, del0 < 0 ? 0 : del0)) { noops++; Check(before == after, "buffremovenoop changed state at step " + step); }
        }
        Check(noops > 1000, "buffremovenoop no-op cases " + noops);
        Check(!BuffRemoveRule.IsNoOp(false, true, 0, true, 0), "buffremovenoop: null count list goes to the game");
        Check(!BuffRemoveRule.IsNoOp(true, false, 0, true, 0) && !BuffRemoveRule.IsNoOp(false, false, 1, true, 0) && !BuffRemoveRule.IsNoOp(false, false, 0, false, 1), "buffremovenoop: active / ref counts / delayed go to the game");
    }

    // catnamecache: product objects own native handles; Dispose zeroes a handle and a freed handle value is reused by a
    // new product of another kind; names change only with a content/locale event, which clears.
    sealed class FakeProduct { public uint Handle; public int Kind; }
    static void CatalogModel()
    {
        Random r = new Random(96);
        List<FakeProduct> live = new List<FakeProduct>();
        Stack<uint> freed = new Stack<uint>();
        uint nextHandle = 1;
        CatalogNameMemo m = new CatalogNameMemo();
        int hits = 0, version = 0;
        for (int step = 0; step < 60000 && sFails < 10; step++)
        {
            int op = r.Next(1000);
            if (op < 2) { version++; m.Clear(); continue; }                                       // new content / locale
            if (op < 30 || live.Count < 20)
            {
                FakeProduct p = new FakeProduct(); p.Kind = r.Next(5000);
                p.Handle = freed.Count > 0 && r.Next(2) == 0 ? freed.Pop() : nextHandle++;   // handle reuse
                live.Add(p); continue;
            }
            if (op < 50) { int k = r.Next(live.Count); FakeProduct p = live[k]; freed.Push(p.Handle); if (r.Next(2) == 0) p.Handle = 0; else live.RemoveAt(k); continue; } // Dispose / finalizer
            FakeProduct q = live[r.Next(live.Count)];
            string game = q.Handle == 0 ? "" : "kind" + q.Kind + "v" + version;
            string got;
            if (m.TryGet(q, q.Handle, out got)) hits++; else { got = game; m.Store(q, q.Handle, got); }
            Check(got == game, "catnamecache at step " + step);
        }
        Check(hits > 10000, "catnamecache hits " + hits);
        FakeProduct a1 = new FakeProduct(), b1 = new FakeProduct(); string nm;
        m.Store(a1, 7, "a"); Check(!m.TryGet(b1, 7, out nm) && m.TryGet(a1, 7, out nm) && nm == "a", "catnamecache: same handle, other product misses");
        m.Store(a1, 0, "z"); Check(!m.TryGet(a1, 0, out nm), "catnamecache: handle 0 never kept");
        Check(m.Clear() && !m.TryGet(a1, 7, out nm) && !m.Clear(), "catnamecache: clear");
        DirectMemo<string> e = new DirectMemo<string>(4);
        Check(!e.Clear(), "DirectMemo: empty clear reports nothing");
        e.Store(1, "a"); string s; Check(e.TryGet(1, out s) && s == "a" && !e.TryGet(2, out s), "DirectMemo hit/miss");
        Check(e.Clear() && !e.TryGet(1, out s), "DirectMemo clear");
        bool threw = false; try { new DirectMemo<int>(3); } catch (ArgumentException) { threw = true; }
        Check(threw, "DirectMemo refuses a non power of two");
    }

    // autroommemo: the game's per-Sim allowed-room cache (recomputed from a rule that reads the Sim's room at recalc time),
    // the lot's portal version, the Sim's current lot. The memo is bumped on every recalc, every cache-version refresh and
    // every new segment, exactly as the hooks do; its answer must equal the game's on every call.
    sealed class FakeSim { public int Id, Room, Lot; public Dictionary<int, KeyValuePair<int, Dictionary<int, bool>>> Cache = new Dictionary<int, KeyValuePair<int, Dictionary<int, bool>>>(); }
    static int[] sLotVersion = new int[4];
    static int sRule;
    static object[] sLots = { new object(), new object(), new object(), new object() };
    static void Recalc(FakeSim s, int lot, int init, RoomMemo m)
    {
        m.Bump(); // H3
        Dictionary<int, bool> d = new Dictionary<int, bool>();
        for (int room = 0; room < 6; room++) d[room] = ((room * 7 + init * 3 + lot + s.Id + sRule) % 5) != 0;
        s.Cache[lot] = new KeyValuePair<int, Dictionary<int, bool>>(sLotVersion[lot], d);
    }
    static bool GameAllowed(FakeSim s, int lot, int room, RoomMemo m)
    {
        if (lot != s.Lot && !GameAllowed(s, s.Lot, 0, m)) return false;
        if (room == -1) room = 0;
        KeyValuePair<int, Dictionary<int, bool>> c;
        if (!s.Cache.TryGetValue(lot, out c) || c.Key != sLotVersion[lot]) { Recalc(s, lot, lot == s.Lot ? s.Room : 0, m); c = s.Cache[lot]; }
        bool v; return c.Value.TryGetValue(room, out v) && v;
    }
    static void RoomModel()
    {
        Random r = new Random(97);
        FakeSim[] sims = new FakeSim[12];
        for (int i = 0; i < sims.Length; i++) { sims[i] = new FakeSim(); sims[i].Id = i; sims[i].Lot = i % 4; }
        RoomMemo m = new RoomMemo();
        int hits = 0, misses = 0, cur = 0;
        for (int step = 0; step < 80000 && sFails < 10; step++)
        {
            int op = r.Next(1000);
            FakeSim s = r.Next(10) == 0 ? sims[r.Next(sims.Length)] : sims[cur]; // mostly the scoring Sim, sometimes another (a social target)
            if (op < 10) { m.Bump(); cur = r.Next(sims.Length); sims[cur].Room = r.Next(6); } // new segment (the Sim may have moved rooms in between)
            else if (op < 13) { m.Bump(); s.Lot = r.Next(4); }                               // new segment, lot changed
            else if (op < 18) { sLotVersion[r.Next(4)]++; m.Bump(); }                        // RefreshObjectCacheUsers / RefreshObjectCaches
            else if (op < 22) { sRule++; Recalc(s, s.Lot, s.Room, m); }                      // public RecalculateAllowedRooms (door lock change)
            else
            {
                int lot = r.Next(4), room = r.Next(-1, 6);
                // hook vs game on copies is not possible (the game call mutates); call the game once, compare with the memo first
                bool memoAns; bool hit = m.TryGet(s, (ulong)s.Id, sLots[s.Lot], (ulong)lot, room, out memoAns);
                bool game = GameAllowed(s, lot, room, m);
                if (hit) { hits++; Check(memoAns == game, "autroommemo hit at step " + step); }
                else { misses++; m.Store(s, (ulong)s.Id, sLots[s.Lot], (ulong)lot, room, game); }
            }
        }
        Check(hits > 20000 && misses > 1000, "autroommemo hits " + hits + " misses " + misses);
        // full table: no store, older entries kept
        RoomMemo f = new RoomMemo(); object o = new object(); bool a;
        for (int i = 0; i < RoomMemo.kSlots; i++) Check(f.Store(o, 1, null, (ulong)i, i, i % 2 == 0), "room store " + i);
        Check(!f.Store(o, 1, null, 999, 999, true), "room memo full: no store");
        Check(f.TryGet(o, 1, null, 5, 5, out a) && !a && !f.TryGet(o, 1, null, 999, 999, out a), "room memo full: old kept, new absent");
        Check(f.TryGet(o, 1, null, 4, 4, out a) && a && !f.TryGet(o, 1, sLots[0], 4, 4, out a), "room memo: another current lot is a miss");
        Check(!f.TryGet(o, 1, null, 0, -1, out a), "room memo: room -1 and 0 kept apart");
        f.Bump(); Check(!f.TryGet(o, 1, null, 4, 4, out a), "room memo: bump drops all");
    }

    static void ScopeAndHistogram()
    {
        PairScope p = new PairScope(); object a = new object(), b = new object();
        Check(p.Renew(a, 1, 7) && !p.Renew(a, 1, 7), "scope: first opens, same stays");
        Check(p.Renew(b, 1, 7) && p.Renew(b, 2, 7) && p.Renew(b, 2, 8), "scope: owner, tick, task each open a new segment");
        p.Reset(); Check(!p.Open && p.Renew(b, 2, 8), "scope: reset reopens");
        Check(!p.Inside(2, 8), "scope: depth 0 is outside"); p.Depth = 1; Check(p.Inside(2, 8) && !p.Inside(3, 8) && !p.Inside(2, 9), "scope: inside only on its tick and task"); p.Depth = 0;
        int[] c = new int[10];
        long[] us = { 999, 1000, 1999, 2000, 4999, 5000, 9999, 10000, 123456, -5 };
        int[] want = { 0, 1, 1, 2, 2, 3, 3, 4, 4, -1 };
        for (int i = 0; i < us.Length; i++) if (want[i] >= 0) Check(SliceHistogram.Bucket(us[i]) == want[i], "bucket " + us[i]);
        foreach (long u in us) SliceHistogram.Add(c, 2, u);
        Check(c[2] == 1 && c[3] == 2 && c[4] == 2 && c[5] == 2 && c[6] == 2 && c[7] == 123456 && c[1] == 0 && c[8] == 0, "histogram counts and max");
    }

    delegate void H(int[] box);
    sealed class Tgt { public int N; public void F(int[] box) { box[0] += 1 + N; } }
    static void InvCacheModel()
    {
        Random r = new Random(115);
        InvListCache cache = new InvListCache();
        int[] c = new int[3];
        Tgt[] t = new Tgt[6]; for (int i = 0; i < t.Length; i++) { t[i] = new Tgt(); t[i].N = i; }
        H field = null;
        for (int step = 0; step < 20000 && sFails < 10; step++)
        {
            int op = r.Next(10);
            if (op < 2) field = (H)Delegate.Combine(field, new H(t[r.Next(t.Length)].F));          // add_PortalPathPlan
            else if (op < 4) field = (H)Delegate.Remove(field, new H(t[r.Next(t.Length)].F));      // remove_PortalPathPlan
            else if (op == 4) cache.Clear();
            else if (field != null)
            {
                Delegate[] want = field.GetInvocationList();
                Delegate[] got = cache.Get(field, c, 0, 1);
                Check(InvListCache.Same(got, want), "invcache list at step " + step);
                int[] gb = new int[1], mb = new int[1];
                foreach (Delegate d in want) ((H)d)(gb);
                foreach (Delegate d in got) ((H)d)(mb);
                Check(gb[0] == mb[0], "invcache dispatch result at step " + step);
            }
        }
        Check(c[0] > 1000 && c[1] > 100, "invcache hits " + c[0] + " rebuilds " + c[1]);
        H one = new H(t[0].F), two = (H)Delegate.Combine(one, new H(t[1].F));
        Delegate[] stale = one.GetInvocationList();
        Check(InvListCache.Check(two, stale, c, 2).Length == 2 && c[2] == 1, "invcache verify: a wrong list counts and the fresh one is used");
        Check(ReferenceEquals(InvListCache.Check(one, stale, c, 2), stale) && c[2] == 1, "invcache verify: same list kept");
        InvListCache e = new InvListCache(); int[] k = new int[3];
        H[] hs = new H[5]; for (int i = 0; i < 5; i++) hs[i] = new H(t[i].F);
        for (int i = 0; i < 5; i++) e.Get(hs[i], k, 0, 1);
        e.Get(hs[0], k, 0, 1); Check(k[1] == 6, "invcache: 5th handler evicted the 1st");
        e.Get(hs[4], k, 0, 1); Check(k[0] == 1, "invcache: newest kept");
    }

    // docs/275 Z1 zdemptyout: random table adds (count step, reset, stereo set), PostWorldStartup removals, instance swaps
    // (load / ClearStatics). The hook answers false / returns only on an empty table; answers and table state must match
    // the game's method run on a copy.
    sealed class ZdInfo { public float TimeOut; public bool Stereo; public bool StereoAlive; }
    static bool ZdGameStereo(Dictionary<ulong, ZdInfo> t, ulong lot)
    {
        ZdInfo v;
        if (t.TryGetValue(lot, out v) && v.Stereo) { if (v.StereoAlive) return true; v.Stereo = false; }
        return false;
    }
    static void ZdGameUpdate(Dictionary<ulong, ZdInfo> t, ulong lot, float dt)
    {
        ZdInfo v;
        if (t.TryGetValue(lot, out v)) { v.TimeOut -= dt; if (v.TimeOut < 0f) v.TimeOut = 0f; }
    }
    static void ZdModel()
    {
        Random r = new Random(275);
        TableKeeper k = new TableKeeper();
        object owner = new object();
        Dictionary<ulong, ZdInfo> game = new Dictionary<ulong, ZdInfo>(), ours = new Dictionary<ulong, ZdInfo>();
        int early = 0;
        for (int step = 0; step < 40000 && sFails < 10; step++)
        {
            int op = r.Next(1000);
            ulong lot = (ulong)r.Next(8);
            if (op < 20) { ZdInfo a = new ZdInfo(), b = new ZdInfo(); a.TimeOut = b.TimeOut = 5f; a.Stereo = b.Stereo = r.Next(2) == 0; a.StereoAlive = b.StereoAlive = r.Next(2) == 0; if (!game.ContainsKey(lot)) { game.Add(lot, a); ours.Add(lot, b); } }
            else if (op < 22) { game.Remove(lot); ours.Remove(lot); }
            else if (op < 23) { owner = new object(); game = new Dictionary<ulong, ZdInfo>(); ours = new Dictionary<ulong, ZdInfo>(); } // new instance, new table
            else if (op < 26) { game.Clear(); ours.Clear(); }
            else
            {
                if (!k.Same(owner)) k.Keep(owner, ours);
                bool empty = TableKeeper.Empty(k.Table);
                if (empty) early++;
                if (op < 500)
                {
                    bool g = ZdGameStereo(game, lot), o = empty ? false : ZdGameStereo(ours, lot);
                    Check(g == o, "zdemptyout stereo at step " + step);
                }
                else
                {
                    float dt = (float)r.NextDouble();
                    ZdGameUpdate(game, lot, dt);
                    if (!empty) ZdGameUpdate(ours, lot, dt);
                }
                ZdInfo gi, oi;
                Check(game.Count == ours.Count && (!game.TryGetValue(lot, out gi) || (ours.TryGetValue(lot, out oi) && gi.TimeOut == oi.TimeOut && gi.Stereo == oi.Stereo)), "zdemptyout table at step " + step);
            }
        }
        Check(early > 3000 && early < 30000, "zdemptyout early-outs " + early);
        Check(!TableKeeper.Empty(null), "zdemptyout: null table is never empty");
        k.Clear();
        Check(!k.Same(owner), "zdemptyout: Clear drops the owner");
    }

    // docs/275 M1 decaymemo: managers with random watched (3) and other buffs, adds bumping the generation before and after
    // the body with a decay check possibly made inside it (OnAddition), removals without a bump, manager swaps (load, travel).
    static void DecayModel()
    {
        Random r = new Random(2751);
        AbsentMemo m = new AbsentMemo();
        List<HashSet<int>> mgrs = new List<HashSet<int>>();
        for (int i = 0; i < 4; i++) mgrs.Add(new HashSet<int>());
        int gen = 0, hits = 0;
        for (int step = 0; step < 60000 && sFails < 10; step++)
        {
            int op = r.Next(1000);
            int mi = r.Next(mgrs.Count);
            HashSet<int> bm = mgrs[mi];
            int buff = r.Next(6); // 0-2 watched
            if (op < 5)
            {
                gen++;
                if (r.Next(2) == 0) DecayCall(m, bm, gen, ref hits, step); // check inside the body, before the Add
                bm.Add(buff);
                gen++;
            }
            else if (op < 40) bm.Remove(buff);
            else if (op < 41) mgrs[mi] = new HashSet<int>(bm); // same buffs, new manager object
            else DecayCall(m, op < 990 ? mgrs[0] : bm, gen, ref hits, step);
        }
        Check(hits > 15000, "decaymemo hits " + hits);
        m.Clear();
        Check(!m.Hit(mgrs[0], gen), "decaymemo: Clear drops the owner");
        Check(!m.Hit(null, gen), "decaymemo: null owner never hits");
    }
    static void DecayCall(AbsentMemo m, HashSet<int> bm, int gen, ref int hits, int step)
    {
        float game = 1f;
        for (int b = 0; b < 3; b++) if (bm.Contains(b)) game *= 0.5f + b;
        float got;
        if (m.Hit(bm, gen)) { got = 1f; hits++; }
        else { got = game; m.Store(bm, gen, !bm.Contains(0) && !bm.Contains(1) && !bm.Contains(2)); }
        Check(got == game, "decaymemo at step " + step);
    }

    static int Main()
    {
        StampRules();
        RefSetModel();
        HoursModel();
        GateRules();
        WildflowerModel();
        PermaModel();
        BuffRemoveModel();
        CatalogModel();
        RoomModel();
        ScopeAndHistogram();
        InvCacheModel();
        ZdModel();
        DecayModel();
        Console.WriteLine(sFails == 0 ? "hotspots selftest: PASS" : "hotspots selftest: FAIL (" + sFails + ")");
        return sFails == 0 ? 0 : 1;
    }
}
