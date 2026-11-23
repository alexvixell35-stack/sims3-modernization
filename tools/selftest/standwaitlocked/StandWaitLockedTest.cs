// Usage: node tools/selftest/standwaitlocked/run.ts  (compiles this with mods/ts3fixes/src/StandWaitLockedCore.cs)
// Offline self-check of StandWaitLockedCore.cs (docs/686 §7): the game's privacy rule for an interaction, what locks a
// destination, the give-up decision, and a model of a wait at a privacy lock run cycle by cycle against the game's
// 20-minute wait. Exit code 1 on failure.
using System;
using Ts3Fixes;

static class StandWaitLockedTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL: " + what); } }

    // InteractionPriorityLevel: Zero, Autonomous, NonCriticalNPCBehavior, UserDirected, High, Privacy, CriticalNPCBehavior, ...
    const int kZero = 0, kAutonomous = 1, kNonCritical = 2, kUser = 3, kHigh = 4, kPrivacy = 5, kCritical = 6, kFire = 7;

    static void Gate()
    {
        Check(!StandWaitLockedCore.PrivacyLocks(false, true, kAutonomous, kPrivacy, kUser), "no interaction: not locked");
        Check(StandWaitLockedCore.PrivacyLocks(true, true, kAutonomous, kPrivacy, kUser), "autonomous: locked");
        Check(StandWaitLockedCore.PrivacyLocks(true, true, kPrivacy, kPrivacy, kUser), "autonomous at Privacy priority: locked");
        Check(!StandWaitLockedCore.PrivacyLocks(true, true, kCritical, kPrivacy, kUser), "autonomous above Privacy: not locked");
        Check(!StandWaitLockedCore.PrivacyLocks(true, false, kUser, kPrivacy, kUser), "a player's order: not locked");
        Check(!StandWaitLockedCore.PrivacyLocks(true, false, kHigh, kPrivacy, kUser) && !StandWaitLockedCore.PrivacyLocks(true, false, kFire, kPrivacy, kUser), "ordered above UserDirected: not locked");
        Check(StandWaitLockedCore.PrivacyLocks(true, false, kNonCritical, kPrivacy, kUser) && StandWaitLockedCore.PrivacyLocks(true, false, kZero, kPrivacy, kUser), "pushed by a script below UserDirected: locked");
        // the rule is the game's expression, for every combination
        for (int a = 0; a < 2; a++)
            for (int level = 0; level < 13; level++)
            {
                bool autonomous = a == 1;
                bool ea = (!autonomous || level <= kPrivacy) && (autonomous || level < kUser);
                Check(StandWaitLockedCore.PrivacyLocks(true, autonomous, level, kPrivacy, kUser) == ea, "gate equals the game's expression (" + autonomous + ", " + level + ")");
            }
    }

    static void Kinds()
    {
        Check(StandWaitLockedCore.LockKind(false, 1, 1, 0) == StandWaitLockedCore.kLockPrivacy, "a privacy situation that holds: privacy");
        Check(StandWaitLockedCore.LockKind(false, 2, 1, 3) == StandWaitLockedCore.kLockPrivacy, "privacy holds whatever the doors say (every portal into the room is locked)");
        Check(StandWaitLockedCore.LockKind(true, 1, 1, 0) == StandWaitLockedCore.kLockPrivacyOpen, "the Sim is inside the room: not a privacy lock");
        Check(StandWaitLockedCore.LockKind(false, 1, 0, 0) == StandWaitLockedCore.kLockPrivacyOpen, "toddler, partner or ordered Sim: the situation does not lock it");
        Check(StandWaitLockedCore.LockKind(false, 1, 0, 1) == StandWaitLockedCore.kLockDoor, "situation does not hold, a door refused: door");
        Check(StandWaitLockedCore.LockKind(false, 0, 0, 1) == StandWaitLockedCore.kLockDoor, "player's door lock: door");
        Check(StandWaitLockedCore.LockKind(false, 0, 0, 0) == StandWaitLockedCore.kLockOther, "nothing known: other");
        Check(StandWaitLockedCore.kLockNames.Length == 5, "a name per lock kind");
    }

    static void Decisions()
    {
        int p = StandWaitLockedCore.kLockPrivacy;
        Check(StandWaitLockedCore.Decide(p, 1, 2, true, true, false) == StandWaitLockedCore.kKeep, "first locked cycle: wait");
        Check(StandWaitLockedCore.Decide(p, 2, 2, true, true, false) == StandWaitLockedCore.kGiveUp, "second locked cycle: give up");
        Check(StandWaitLockedCore.Decide(p, 9, 2, true, true, false) == StandWaitLockedCore.kGiveUp, "later cycles too (a cycle that yielded first)");
        Check(StandWaitLockedCore.Decide(p, 1, 1, true, true, false) == StandWaitLockedCore.kGiveUp, "tuned to 1: first cycle");
        Check(StandWaitLockedCore.Decide(p, 99, 0, true, true, false) == StandWaitLockedCore.kKeep, "tuned to 0: never");
        Check(StandWaitLockedCore.Decide(p, 2, 2, false, true, false) == StandWaitLockedCore.kWould && StandWaitLockedCore.Decide(p, 2, 2, false, false, false) == StandWaitLockedCore.kWould, "switch off: counted for any Sim");
        Check(StandWaitLockedCore.Decide(p, 2, 2, true, false, false) == StandWaitLockedCore.kExempt, "household without its switch: exempt");
        Check(StandWaitLockedCore.Decide(p, 2, 2, true, true, true) == StandWaitLockedCore.kYieldFirst, "a push or cancel standwaitfix answers goes first");
        for (int kind = 0; kind < 5; kind++)
            if (kind != p) Check(StandWaitLockedCore.Decide(kind, 99, 2, true, true, false) == StandWaitLockedCore.kKeep, "lock kind " + kind + ": the game's wait");
    }

    // One wait: cycles of cycleMin sim-minutes, the lock ends after lockMin (never when negative). Returns the sim-minutes stood.
    static float Wait(bool fixOn, int giveUp, float cycleMin, float lockMin, float durationMin, out bool walkedOn)
    {
        float t = 0f;
        int run = 0;
        walkedOn = false;
        do
        {
            t += cycleMin;
            if (lockMin >= 0f && t >= lockMin) { walkedOn = true; return t; }  // the game's replan succeeds
            run++;
            if (StandWaitLockedCore.Decide(StandWaitLockedCore.kLockPrivacy, run, giveUp, fixOn, true, false) == StandWaitLockedCore.kGiveUp) return t;
        }
        while (t < durationMin);
        return t;
    }

    static void Model()
    {
        bool walked;
        float off = Wait(false, 2, 1.6f, -1f, 20f, out walked);
        Check(off >= 20f && off < 22f && !walked, "off: the game's wait runs out (20 sim-minutes, as in the storms: 21-22)");
        float on = Wait(true, 2, 1.6f, -1f, 20f, out walked);
        Check(on > 3f && on < 3.5f && !walked, "on: two cycles");
        on = Wait(true, 2, 1.6f, 1.5f, 20f, out walked);
        Check(walked && on < 2f, "on: a lock that ends in the first cycle lets the Sim walk on");
        on = Wait(true, 2, 1.6f, 10f, 20f, out walked);
        Check(!walked, "on: a lock that ends later is not waited for");
        off = Wait(false, 2, 1.6f, 10f, 20f, out walked);
        Check(walked && off >= 10f, "off: the game stands until the lock ends");
        Random r = new Random(686);
        for (int i = 0; i < 500; i++)
        {
            int n = 1 + r.Next(4);
            float cycle = 1f + (float)r.NextDouble() * 3f, lockMin = r.Next(3) == 0 ? -1f : (float)r.NextDouble() * 30f;
            bool w1, w2;
            float a = Wait(true, n, cycle, lockMin, 20f, out w1), b = Wait(false, n, cycle, lockMin, 20f, out w2);
            Check(a <= b + 0.001f, "the fix never lengthens a wait");
            Check(a <= n * cycle + 0.001f || a <= cycle + 0.001f, "on: at most the tuned cycles");
            if (w1) Check(w2 && Math.Abs(a - b) < 0.001f, "a wait the fix lets walk on is the game's wait");
        }
    }

    static int Main()
    {
        Gate(); Kinds(); Decisions(); Model();
        Console.WriteLine(sFails == 0 ? "standwaitlocked selftest: ok" : "standwaitlocked selftest: " + sFails + " FAILED");
        return sFails == 0 ? 0 : 1;
    }
}
