using System;
using Ts3Fixes;

// Offline self-check of ShuffleboardNpcCapCore (docs/647).
static class ShuffleboardNpcCapTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; if (sFails < 20) Console.WriteLine("FAIL " + what); } }

    const long H = 100;  // ticks per sim-hour in this test

    static ShuffleboardNpcCapCore.Tun Tun()
    {
        ShuffleboardNpcCapCore.Tun t = new ShuffleboardNpcCapCore.Tun();
        t.RoundsCap = 10; t.CapTicks = 3 * H; t.GapTicks = 1 * H; t.NightFrom = 23; t.NightTo = 6; t.CooldownTicks = 2 * H;
        return t;
    }

    static void Main()
    {
        ShuffleboardNpcCapCore.Tun t = Tun();
        ShuffleboardNpcCapCore.Board b;

        // night window
        Check(ShuffleboardNpcCapCore.Night(23, 23, 6) && ShuffleboardNpcCapCore.Night(0, 23, 6) && ShuffleboardNpcCapCore.Night(5, 23, 6), "23..6: 23, 0, 5 are night");
        Check(!ShuffleboardNpcCapCore.Night(6, 23, 6) && !ShuffleboardNpcCapCore.Night(16, 23, 6), "23..6: 6, 16 are day");
        Check(ShuffleboardNpcCapCore.Night(2, 1, 4) && !ShuffleboardNpcCapCore.Night(4, 1, 4), "1..4 without wrap");
        Check(!ShuffleboardNpcCapCore.Night(3, 5, 5), "from == to: no night window");

        // pure rule
        Check(ShuffleboardNpcCapCore.Rule(0, 10 * H, 2, t) == ShuffleboardNpcCapCore.kNone, "no round done: never capped, even at night or old");
        Check(ShuffleboardNpcCapCore.Rule(1, 0, 23, t) == ShuffleboardNpcCapCore.kNight, "a round done at 11 PM: night");
        Check(ShuffleboardNpcCapCore.Rule(9, H, 16, t) == ShuffleboardNpcCapCore.kNone, "9 rounds, 1 h, afternoon: runs");
        Check(ShuffleboardNpcCapCore.Rule(10, H, 16, t) == ShuffleboardNpcCapCore.kGames, "10 rounds (2 games of 5): games");
        Check(ShuffleboardNpcCapCore.Rule(2, 3 * H, 16, t) == ShuffleboardNpcCapCore.kHours, "3 sim-hours: hours");
        ShuffleboardNpcCapCore.Tun off = t; off.RoundsCap = 0; off.CapTicks = 0; off.NightFrom = off.NightTo = 0;
        Check(ShuffleboardNpcCapCore.Rule(1000, 1000 * H, 1, off) == ShuffleboardNpcCapCore.kNone, "every cap off: runs");

        // solo streak from 16:00: one round start every 30 sim-min, the age cap ends it after 3 h
        ShuffleboardNpcCapCore core = new ShuffleboardNpcCapCore();
        long now = 0; int v = 0, starts = 0;
        for (int i = 0; i < 40; i++)
        {
            int hour = 16 + (int)(now / H);
            v = core.RoundStart(7, now, hour, t, out b);
            if (v != ShuffleboardNpcCapCore.kNone) break;
            starts++;
            now += H / 2;
        }
        Check(v == ShuffleboardNpcCapCore.kHours && starts == 6 && now == 300, "solo: hours cap at the round start after 3 h (got " + v + ", " + starts + ", " + now + ")");
        // the other player of the board at the same round start: still capped, rounds unchanged
        Check(core.RoundStart(7, now, 19, t, out b) == ShuffleboardNpcCapCore.kHours && b.Rounds == 6, "capped streak stays capped");
        // a round start after the gap: new streak
        Check(core.RoundStart(7, now + H + 1, 20, t, out b) == ShuffleboardNpcCapCore.kNone && b.Rounds == 1, "gap: new streak");
        // clock back: new streak
        Check(core.RoundStart(7, 5, 12, t, out b) == ShuffleboardNpcCapCore.kNone && b.Rounds == 1 && b.First == 5, "clock back: new streak");

        // night: first round runs, the second start caps
        ShuffleboardNpcCapCore n = new ShuffleboardNpcCapCore();
        Check(n.RoundStart(9, 0, 23, t, out b) == ShuffleboardNpcCapCore.kNone, "night: first round runs");
        Check(n.RoundStart(9, 10, 23, t, out b) == ShuffleboardNpcCapCore.kNight, "night: next round start ends");

        // games cap with quick rounds (two teams)
        ShuffleboardNpcCapCore g = new ShuffleboardNpcCapCore();
        long tg = 0; int vg = 0, sg = 0;
        for (int i = 0; i < 20; i++) { vg = g.RoundStart(3, tg, 12, t, out b); if (vg != 0) break; sg++; tg += 5; }
        Check(vg == ShuffleboardNpcCapCore.kGames && sg == 10, "two games of 5 rounds then games cap");
        // boards are separate
        Check(g.RoundStart(4, tg, 12, t, out b) == ShuffleboardNpcCapCore.kNone, "another board: own streak");

        // cooldown
        ShuffleboardNpcCapCore c = new ShuffleboardNpcCapCore();
        Check(!c.Cooling(1, 0, t.CooldownTicks), "never ended: no cooldown");
        c.Ended(1, 100, t.CooldownTicks);
        Check(c.Cooling(1, 100, t.CooldownTicks) && c.Cooling(1, 299, t.CooldownTicks), "within 2 h: refused");
        Check(!c.Cooling(1, 300, t.CooldownTicks) && c.CoolCount == 0, "after 2 h: allowed, entry gone");
        c.Ended(2, 100, t.CooldownTicks);
        Check(!c.Cooling(2, 50, t.CooldownTicks), "clock back: allowed");
        c.Ended(3, 100, 0);
        Check(!c.Cooling(3, 100, 0) && c.CoolCount == 0, "cooldown 0: nothing kept");

        // bounded tables
        ShuffleboardNpcCapCore m = new ShuffleboardNpcCapCore();
        for (ulong i = 1; i <= 1000; i++) { m.RoundStart(i, 0, 12, t, out b); m.Ended(i, 0, t.CooldownTicks); }
        Check(m.CoolCount <= ShuffleboardNpcCapCore.kMax, "cooldown table bounded");
        Check(m.RoundStart(5000, 0, 12, t, out b) == ShuffleboardNpcCapCore.kNone, "new board past the bound works");

        if (sFails == 0) Console.WriteLine("shuffleboardnpccap selftest: OK");
        else { Console.WriteLine("shuffleboardnpccap selftest: " + sFails + " FAILED"); Environment.Exit(1); }
    }
}
