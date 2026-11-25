using System;

namespace Ts3Fixes
{
    // Game-independent rules of the facility planner (FacilityPlan.cs, docs/686 §36): facilityplan / facilityplanhh,
    // facilityquick / facilityquickhh, facilitychain / facilitychainhh. Offline self-check: tools/selftest/facilityplan.
    // Every motive number below is EA's own (Motives XML in GameplayData.package, docs/686 §36.2); nothing here changes a
    // decay rate or a threshold, it only predicts with them.
    public static class FacilityPlanCore
    {
        // facility kinds
        public const int kToilet = 0, kShower = 1, kBath = 2, kSink = 3, kFridge = 4, kKinds = 5;  // the fridge: pick only (its line is EA's 3-Sim SimQueue)
        public static readonly string[] kKindNames = { "toilet", "shower", "bath", "sink", "fridge" };

        // decisions
        public const int kWait = 0, kSwitch = 1, kDefer = 2, kKeepEa = 3, kWouldSwitch = 4, kWouldDefer = 5, kExempt = 6;
        public const int kWhyNone = 0, kWhyNotHuman = 1, kWhyPlayer = 2, kWhyUrgentNoBetter = 3, kWhyCooldown = 4, kWhyNoBetter = 5, kWhyShort = 6, kWhyUrgent = 7;
        public static readonly string[] kWhyNames = { "", "not a human", "player's order", "urgent, nothing frees in time (EA's way)", "cooldown", "no better object", "short wait", "urgent" };

        // ---- EA's bladder bands (adult; Bladder tuning, VariableDecayByMoodlet) ----
        public const float kBladderUrgent = -60f;     // Intensity curve knee (0.25 -> 0.995 at -60); HasToPee from -61
        public const float kBladderReally = -90f;     // ReallyHasToPee from -91
        public const float kBladderAccident = -100f;  // Motive.MotiveDistress at Tuning.Min: Sim.BladderFailure
        public const float kRateHasToPee = 14.5f;     // points per sim-hour in -61..-90
        public const float kRateReally = 9f;          // points per sim-hour in -91..-100
        public const float kHygieneUrgent = -60f;     // Grungy from -61 (Smelly from -81); no accident
        public const float kHungerUrgent = -60f;      // Hungry from -61 (EA's hunger moodlet bands; starving at -100 is EA's own, kept)

        // ---- planner tunables (not EA balance: they only rank objects and time) ----
        public const float kWalkMetersPerMinute = 1.5f;  // a walk at speed 1 (1 sim-min = 1 s); s565 logs to check
        public const float kLevelMinutes = 1.5f;         // one floor up or down by stairs
        public const float kHystMinutes = 3f;            // a switch must save at least this...
        public const float kHystFraction = 0.25f;        // ...and this part of the current wait
        public const float kCooldownMinutes = 20f;       // per Sim, after a switch or a defer
        public const float kDeferMinutes = 8f;           // a wait at least this long is worth doing something else first
        public const float kUrgentMarginMinutes = 15f;   // urgent: the urgent level comes within the wait plus this
        public const float kPlayerLongMinutes = 10f;     // a player's order moves only to a free twin past this wait (lineswitch's rule)
        public const float kPickLow = 0.1f;              // autonomy score factor for an object much later than the best one (EA's in-use factor is 0.2)
        public const float kPickDefer = 0.02f;           // every object of the kind is late and the Sim is not urgent: another action wins
        public const float kInfinite = 1e6f;

        // ---- facilityquick ----
        public const float kQuickMinMinutes = 5f;        // never cut a use younger than this
        public const float kQuickHygiene = 70f;          // top of EA's neutral Hygiene band (-60..70; SqueakyClean from 81): 85 % of the bar
        public const float kQuickBladder = 70f;          // the same bar for the toilet
        public const float kQuickUrgentMinutes = 30f;    // a waiter whose urgent level comes within this (or already there)

        // ---- facilitychain ----
        public const float kChainSoonMinutes = 120f;     // the other need reaches its urgent level within 2 sim-hours
        public const float kChainLowValue = 0f;          // or is below the middle of the bar already

        // Sim-minutes until a motive falls to `level` at `ratePerHour` (EA's Motive.Decay, negative while it falls).
        public static float MinutesTo(float value, float level, float ratePerHour)
        {
            if (value <= level) return 0f;
            float r = -ratePerHour;
            if (!(r > 0.01f)) return kInfinite;  // NaN too
            return (value - level) / r * 60f;
        }

        // Sim-minutes until the bladder accident: the live rate down to -60, then EA's two band rates.
        public static float MinutesToAccident(float value, float rateNow)
        {
            if (value <= kBladderAccident) return 0f;
            float m = 0f;
            if (value > kBladderUrgent)
            {
                float a = MinutesTo(value, kBladderUrgent, rateNow);
                if (a >= kInfinite) return kInfinite;
                m += a; value = kBladderUrgent;
            }
            if (value > kBladderReally) { m += (value - kBladderReally) / kRateHasToPee * 60f; value = kBladderReally; }
            m += (value - kBladderAccident) / kRateReally * 60f;
            return m;
        }

        public static float Travel(float meters, int levels)
        {
            if (!(meters >= 0f)) meters = 0f;
            if (levels < 0) levels = -levels;
            return meters / kWalkMetersPerMinute + levels * kLevelMinutes;
        }

        // When this Sim could start at an object: it walks while the user and the line ahead finish.
        public static float Eta(float travel, float busyMinutes, float aheadMinutes)
        {
            float q = Math.Max(0f, busyMinutes) + Math.Max(0f, aheadMinutes);
            return Math.Max(travel, q);
        }

        // Hysteresis: a candidate is better only by a clear margin, so two close bathrooms never ping-pong.
        public static bool Better(float candEta, float curEta)
        {
            return curEta - candEta >= Math.Max(kHystMinutes, kHystFraction * curEta);
        }

        public static bool Urgent(float minutesToUrgent, float eta) { return minutesToUrgent <= eta + kUrgentMarginMinutes; }

        // One check of a Sim waiting in a facility line.
        //   human, autonomous   a human Sim; it picked the action itself
        //   inScope             NPC with facilityplan, household/selected also with facilityplanhh
        //   cooldownOk          no planner move of this Sim within kCooldownMinutes
        //   curEta              its own object; bestEta: the best other object of the kind (kInfinite: none);
        //   bestFree            that object is free now (no user, no line)
        //   minutesToUrgent     its need's urgent level; minutesToAccident: kInfinite for needs without one
        public static int Decide(bool on, bool human, bool autonomous, bool inScope, bool cooldownOk, float curEta, float bestEta, bool bestFree,
            float minutesToUrgent, float minutesToAccident, out int why)
        {
            why = kWhyNone;
            if (!human) { why = kWhyNotHuman; return kWait; }
            bool better = bestEta < kInfinite && Better(bestEta, curEta);
            if (!autonomous)
            {
                // a player's order keeps its object unless it waits long and an identical free one exists
                if (!(better && bestFree && curEta >= kPlayerLongMinutes)) { why = kWhyPlayer; return kWait; }
            }
            if (!better && minutesToAccident < curEta) { why = kWhyUrgentNoBetter; return kKeepEa; }
            if (!cooldownOk) { why = kWhyCooldown; return kWait; }
            if (better) return !on ? kWouldSwitch : inScope ? kSwitch : kExempt;
            if (!autonomous) { why = kWhyPlayer; return kWait; }
            if (curEta < kDeferMinutes) { why = kWhyShort; return kWait; }
            if (Urgent(minutesToUrgent, curEta)) { why = kWhyUrgent; return kWait; }
            return !on ? kWouldDefer : inScope ? kDefer : kExempt;
        }

        // The autonomy score factor for one object at pick time (multiplies EA's GetInUseAutonomyMultiplier).
        //   eta: this object; bestEta: the best object of the kind; bestWait: the least time any object of the kind keeps the
        //   Sim waiting after its walk (the walk is spent on any other action too, so it never makes a facility "late");
        //   urgent: the Sim's need comes before bestEta plus the margin
        public static float PickFactor(float eta, float bestEta, float bestWait, bool urgent)
        {
            if (!urgent && bestWait >= kDeferMinutes) return kPickDefer;  // all busy past the walk: do something else first, come back
            if (eta > bestEta && Better(bestEta, eta)) return kPickLow;   // another object frees clearly sooner
            return 1f;
        }

        // facilityquick: may the current user end its use now (EA's own ExitReason.Finished, the normal tail)?
        //   kind: kToilet uses Bladder, kShower / kBath Hygiene; value: that motive; minutes: the use so far
        //   waiterUrgent: a Sim waits for this object (or its room) with its urgent level within kQuickUrgentMinutes
        public static bool QuickEnd(int kind, float value, float minutes, bool waiterUrgent)
        {
            if (!waiterUrgent || !(minutes >= kQuickMinMinutes)) return false;
            if (kind == kToilet) return value >= kQuickBladder;
            if (kind == kShower || kind == kBath) return value >= kQuickHygiene;
            return false;
        }

        public static bool WaiterUrgent(float minutesToUrgent) { return minutesToUrgent <= kQuickUrgentMinutes; }

        // facilitychain: after one use, is the other need due soon enough to do it in the same visit?
        public static bool ChainDue(float otherValue, float otherMinutesToUrgent, float otherMax)
        {
            if (otherValue >= otherMax) return false;
            return otherValue <= kChainLowValue || otherMinutesToUrgent <= kChainSoonMinutes;
        }

        // Remaining minutes of a use from its motive: (max - value) / rate, rate from the gain seen so far (points per
        // sim-minute); prior when no gain is seen yet. Never below 0.
        public static float Remaining(float max, float value, float gainPerMinute, float prior, float minutesSoFar)
        {
            if (gainPerMinute > 0.05f) return Math.Max(0f, (max - value) / gainPerMinute);
            return Math.Max(0f, prior - Math.Max(0f, minutesSoFar));
        }

        // Prior use length per kind (sim-min), before a learned mean exists. Toilet: EA's LerpToFill over
        // kMaxLengthUseToilet = 30 fills from any start; the others from the waits seen in storms s533-s536.
        public static float Prior(int kind)
        {
            switch (kind) { case kToilet: return 30f; case kShower: return 15f; case kBath: return 30f; case kFridge: return 2f; default: return 2f; }
        }

        // Learned mean (exponential, 1/4 weight for the new sample), kept in a sane range.
        public static float Learn(float mean, float sample)
        {
            if (!(sample > 0.2f) || sample > 180f) return mean;
            if (!(mean > 0f)) return sample;
            return mean * 0.75f + sample * 0.25f;
        }
    }
}
