// Offline self-check of JobYield's step-aside loop (mods/ts3fixes/src/StepAsideCore.cs, docs/183 §7): every way out of the
// sleep sends exactly one closing message, and a pending jig keeps the plan synchronous.
using System;
using Ts3Fixes;

static class StepAsideTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    // RouteBackoff.cs StepAside's loop with scripted JOBBUSY answers; throwAt = the sleep that throws (a reset), 0 = none.
    // Returns the low word of each message sent after the first busy answer, in order.
    static string Run(bool[] answers, int msPerTick, int capMs, int throwAt, out StepAsideLoop l)
    {
        string sent = "";
        int now = 1000, i = 0;
        l = default(StepAsideLoop);
        l.Begin(now, capMs);
        try
        {
            try
            {
                for (;;)
                {
                    if (throwAt != 0 && l.Ticks + 1 == throwAt) throw new InvalidOperationException("reset");
                    now += msPerTick;
                    if (!l.AfterSleep(now)) break;
                    sent += "0";
                    if (!l.Answer(i < answers.Length && answers[i++])) break;
                }
            }
            finally
            {
                int op = l.FinalOp();
                if (op != StepAsideLoop.kOpNone) sent += op.ToString();
            }
        }
        catch (InvalidOperationException) { }
        return sent;
    }

    static int Main()
    {
        StepAsideLoop l;
        Check(Run(new bool[] { true, true, false }, 16, 10000, 0, out l) == "000" && l.End == StepAsideLoop.kOver && l.Ticks == 3,
            "job over after three ticks: the last poll is the closing message");
        Check(Run(new bool[] { false }, 16, 10000, 0, out l) == "0" && l.End == StepAsideLoop.kOver && l.Ticks == 1, "one tick");
        Check(Run(new bool[] { true, true, true, true, true, true }, 4000, 10000, 0, out l) == "001" && l.End == StepAsideLoop.kCap && l.Ticks == 3,
            "cap: no poll after the last sleep, the end message instead");
        Check(Run(new bool[] { true, true }, 16, 10000, 2, out l) == "02" && l.End == StepAsideLoop.kReset && l.Ticks == 1,
            "reset in the second sleep: drop");
        Check(Run(new bool[] { true }, 16, 10000, 1, out l) == "2" && l.End == StepAsideLoop.kReset, "reset in the first sleep: drop");
        l = default(StepAsideLoop);
        l.Begin(unchecked((int)0x7FFFFFF0), 10000);
        Check(l.AfterSleep(unchecked((int)0x80000010)) && l.Ticks == 1, "the tick count wraps: still inside the cap");
        Check(l.FinalOp() == StepAsideLoop.kOpDrop, "left before any answer: drop");
        Check(!StepAsideCore.KeepSync(0, 0) && StepAsideCore.KeepSync(1, 0) && StepAsideCore.KeepSync(0, 2), "jig of the task's Sim or the route's Sim keeps the plan sync");
        Check(StepAsideCore.EndName(0) == "job over" && StepAsideCore.EndName(1) == "cap" && StepAsideCore.EndName(2) == "reset" && StepAsideCore.EndName(9) == "?", "end names");
        Console.WriteLine(fails == 0 ? "stepaside self-check ok" : "stepaside self-check: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
