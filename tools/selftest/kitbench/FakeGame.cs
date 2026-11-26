// Fake game for the kit benchmark self-check (tools/selftest/kitbench/run.ts): the real ts3agent Mailbox.cs + Dispatcher.cs +
// Json.cs in an x86 exe, polled every 10 ms like a sim tick, with one "ping" command. Exits after 60 s.
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts3Agent
{
    public static class FakeGame
    {
        public static void Main()
        {
            Dispatcher.Register("ping", delegate (Args a) { Dictionary<string, object> r = new Dictionary<string, object>(); r["pong"] = true; r["echo"] = a.Raw("echo"); return r; });
            Mailbox.Open();
            for (int i = 0; i < 6000; i++) { Mailbox.Poll(); Thread.Sleep(10); }
        }
    }
}
