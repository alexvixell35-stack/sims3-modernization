// Offline self-check of the managed lot hub (mods/ts3fixes/src/LotHubCore.cs, docs/321 §9): a fake op 49 queue with the
// native semantics (pop, overflow first, kind, 32 id bits); dispatch order, once per event, once per world event, faults.
using System;
using System.Collections.Generic;
using Ts3Fixes;

static class LotHubTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    // fake native queue: (kind, id) pairs; kind 0 = overflow
    static readonly Queue<KeyValuePair<int, uint>> q = new Queue<KeyValuePair<int, uint>>();
    static KeyValuePair<int, uint> cur;
    static int calls;
    static bool Pop() { calls++; if (q.Count == 0) return false; cur = q.Dequeue(); return true; }
    static bool Over() { calls++; return cur.Key == 0; }
    static bool Rem() { calls++; return cur.Key == LotHubCore.Remove; }
    static bool IdBit(int b) { calls++; return ((cur.Value >> b) & 1) != 0; }
    static int sinkN;
    static void Sink(int k, uint id) { sinkN++; }
    static void WorldSink(bool load) { }
    static int Poll(int max) { return LotHubCore.Poll(Pop, Over, Rem, IdBit, max); }

    static int Main()
    {
        List<string> log = new List<string>();
        LotHubCore.LotFn a = delegate(int k, uint id) { log.Add("a" + k + ":" + id.ToString("X")); };
        LotHubCore.LotFn b = delegate(int k, uint id) { log.Add("b" + k + ":" + id.ToString("X")); };
        LotHubCore.LotFn bad = delegate(int k, uint id) { throw new InvalidOperationException(); };
        LotHubCore.WorldFn w = delegate(bool load) { log.Add(load ? "wl" : "wq"); };

        Check(!LotHubCore.HasLotSubscribers, "no subscribers at start");
        Check(LotHubCore.SubscribeLot(a) && !LotHubCore.SubscribeLot(a) && !LotHubCore.SubscribeLot(null), "same delegate once, null refused");
        Check(LotHubCore.SubscribeLot(bad) && LotHubCore.SubscribeLot(b) && LotHubCore.HasLotSubscribers, "three lot subscribers");
        Check(LotHubCore.SubscribeWorld(w) && !LotHubCore.SubscribeWorld(w), "world subscriber once");

        // idle frame: one native call
        calls = 0;
        Check(Poll(16) == 0 && calls == 1 && log.Count == 0, "idle frame = one pop, no dispatch");

        // two events: arrival order, subscribe order, full 32-bit id, throwing consumer skipped
        q.Enqueue(new KeyValuePair<int, uint>(LotHubCore.Add, 0x80000005));
        q.Enqueue(new KeyValuePair<int, uint>(LotHubCore.Remove, 7));
        calls = 0;
        Check(Poll(16) == 2, "two events dispatched");
        Check(string.Join(",", log.ToArray()) == "a1:80000005,b1:80000005,a2:7,b2:7", "order: event then subscriber (" + string.Join(",", log.ToArray()) + ")");
        Check(calls == 2 * 35 + 1, "35 calls per event + final pop (" + calls + ")");
        Check(LotHubCore.Faults == 2 && LotHubCore.LotEvents == 2, "faults counted per throwing call, events once each");
        log.Clear();
        Check(Poll(16) == 0 && log.Count == 0, "drained: nothing twice");

        // overflow: one "every lot" event, no id asks
        q.Enqueue(new KeyValuePair<int, uint>(0, 0));
        calls = 0;
        Check(Poll(16) == 1 && string.Join(",", log.ToArray()) == "a0:0,b0:0" && calls == 3, "overflow = kind 0, lot 0, 2 asks + final pop");
        log.Clear();

        // per-frame cap: the rest waits a frame, nothing lost or doubled
        for (uint i = 1; i <= 20; i++) q.Enqueue(new KeyValuePair<int, uint>(LotHubCore.Add, i));
        Check(Poll(16) == 16 && q.Count == 4 && Poll(16) == 4 && log.Count == 40, "cap 16 per frame, rest next frame");
        Check(log[0] == "a1:1" && log[39] == "b1:14", "cap keeps order");
        log.Clear();

        // world events: once per call, generation moves
        int g = LotHubCore.WorldGen;
        LotHubCore.World(true);
        LotHubCore.World(false);
        Check(LotHubCore.WorldGen == g + 2 && string.Join(",", log.ToArray()) == "wl,wq", "world load then quit, once each");

        // no leak: 50 load / quit cycles, each consumer subscribing again on every load (a fresh delegate instance of the same
        // method), events in between: the subscriber count stays flat
        int subs = LotHubCore.Subscribers;
        for (int c = 0; c < 50; c++)
        {
            LotHubCore.World(true);
            LotHubCore.SubscribeLot(new LotHubCore.LotFn(Sink));
            LotHubCore.SubscribeWorld(new LotHubCore.WorldFn(WorldSink));
            q.Enqueue(new KeyValuePair<int, uint>(LotHubCore.Add, (uint)c));
            Poll(16);
            LotHubCore.World(false);
        }
        Check(LotHubCore.Subscribers == subs + 2, "50 load/quit cycles with re-subscribe: count flat (" + LotHubCore.Subscribers + ")");
        Check(sinkN == 50, "re-subscribed consumer called once per event (" + sinkN + ")");
        Check(LotHubCore.UnsubscribeLot(new LotHubCore.LotFn(Sink)) && LotHubCore.UnsubscribeWorld(new LotHubCore.WorldFn(WorldSink)) && LotHubCore.Subscribers == subs, "unsubscribe by an equal delegate");
        // cap: the table never grows past kMax
        LotHubCore.ResetForTest();
        for (int i = 0; i < LotHubCore.kMax + 5; i++) { uint k = (uint)i; LotHubCore.SubscribeLot(delegate(int kk, uint id) { log.Add(k.ToString()); }); }
        Check(LotHubCore.Subscribers == LotHubCore.kMax, "lot table capped at kMax");

        // task lifetime across mode changes (Edit Town, CAS, Build / Buy): wait without popping, drain on the first live
        // frame, end only on world quit / replacement / last unsubscribe; re-arm when the armed task is gone
        LotHubCore.ResetForTest();
        log.Clear();
        Check(LotHubCore.TaskStep(true, true, true) == LotHubCore.StepEnd, "no subscriber: task ends");
        Check(!LotHubCore.ShouldArm(true, true, false, false), "no subscriber: not armed");
        LotHubCore.SubscribeLot(a);
        Check(LotHubCore.ShouldArm(true, true, false, false), "first subscriber in a live world: armed");
        Check(!LotHubCore.ShouldArm(false, true, false, false) && !LotHubCore.ShouldArm(true, false, false, false), "no world or no native: not armed");
        Check(!LotHubCore.ShouldArm(true, true, true, true), "running task: not armed twice");
        Check(LotHubCore.ShouldArm(true, true, true, false), "armed task destroyed by a mode change: re-armed");
        Check(LotHubCore.TaskStep(true, true, true) == LotHubCore.StepPoll, "live: poll");
        Check(LotHubCore.TaskStep(true, true, false) == LotHubCore.StepWait, "Edit Town / CAS / Build / Buy: wait, task stays");
        Check(LotHubCore.TaskStep(true, false, false) == LotHubCore.StepEnd && LotHubCore.TaskStep(false, true, true) == LotHubCore.StepEnd, "world quit or replaced: end");
        // frames: 3 events queue while in Edit Town (nothing popped), then the first live frame drains them in order
        for (uint i = 1; i <= 3; i++) q.Enqueue(new KeyValuePair<int, uint>(LotHubCore.Add, i));
        calls = 0;
        for (int f = 0; f < 10; f++) if (LotHubCore.TaskStep(true, true, false) == LotHubCore.StepPoll) Poll(16);
        Check(calls == 0 && q.Count == 3 && log.Count == 0, "Edit Town frames: no native call, events kept");
        if (LotHubCore.TaskStep(true, true, true) == LotHubCore.StepPoll) Poll(16);
        Check(q.Count == 0 && string.Join(",", log.ToArray()) == "a1:1,a1:2,a1:3", "back to live: drained in order (" + string.Join(",", log.ToArray()) + ")");
        log.Clear();

        LotHubCore.ResetForTest();
        Check(!LotHubCore.HasLotSubscribers && LotHubCore.WorldGen == 0, "reset");
        Console.WriteLine(fails == 0 ? "lothub selftest: ok" : "lothub selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
