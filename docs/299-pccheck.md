# 299. `[PcCheck]`: is this PC able to give the game its CPU? (2026-11-20)

Status: built + host tests (`TestPcCheck`); master port on `feature/pccheck-1120` for next week's build (not v0.3.0). The same
code ships first in the docs/298 tester diag kit (`feature/modtime-diag-1120`). On master no sampler names the sim thread,
so the sim-thread CPU column reads 0 there. Ledger §544 (I-544-4, I-544-5).

## 1. Why

Rodney 2026-11-20: "Add a CPU check and PC performance check to double-check if your PC is throttled itself so it can't handle
the Sims 3 performance improvements Modernization uses, like multithreading." A tester log has only the CPU name and thread
count; whether the clock is limited, a power-saver plan, battery, affinity or other programs cut the game's CPU is unknown.

## 2. What it measures (user-mode APIs only, no admin, no driver, changes nothing)

At start: CPU name, logical / physical cores (SMT), RAM total / free, power plan name (`PowerGetActiveScheme`,
`PowerReadFriendlyName`) and the Windows power-mode overlay (`PowerGetEffectiveOverlayScheme`), AC or battery
(`GetSystemPowerStatus`), TS3W affinity (`GetProcessAffinityMask`), priority class, efficiency mode
(`GetProcessInformation(ProcessPowerThrottling)`, EXECUTION_SPEED set), the Game Mode registry value.

Every 10 s (own thread, below normal): per-core `CurrentMhz` / `MaxMhz` / `MhzLimit` (`CallNtPowerInformation
(ProcessorInformation)`); system CPU (`GetSystemTimes`) minus TS3W (`GetProcessTimes`) = other programs; per-process CPU
(Toolhelp + `GetProcessTimes`, `PROCESS_QUERY_LIMITED_INFORMATION`) for the top 3; TS3W threads >= 50% of a core and who
started them (`ThreadQuerySetWin32StartAddress` -> ts3native / TS3W.exe / other module); the sim thread's CPU vs wall when
docs/298 names it.

Clock samples count only while TS3W uses at least half a core (an idle PC clocks down by design). Limited = some core's
`MhzLimit < MaxMhz` (firmware / OS limit) or the fastest core under 90% of `MaxMhz` [inferred I-544-5].

Clock source (0.36.276-modtime2): Windows 11 reports `CurrentMhz` = the base clock and `MhzLimit` 0 [verified: smoke
2026-11-20, i7-11800H, 2304 / 0 in all 100 samples], so the clock comes from PDH when the counters answer (pdh.dll loaded at
run time on the pccheck thread): `\Processor Information(_Total)\% Processor Performance` (effective clock in % of base,
above 100 under boost, read with PDH_FMT_NOCAP100), `Processor Frequency` (base MHz) and `Performance Limit Flags`
(non-zero = a firmware / OS limit). A busy sample is slow when its performance is below 85% of the session's best busy
sample (`PcPerfSlow`, host fixtures); limited = Performance Limit Flags set. Without PDH the CallNtPowerInformation rule
stays. Power plan name: `PowerReadFriendlyName` is now read with its own size (the stored name took 258 bytes for "High
Performance" here, so the fixed buffer failed and printed "?"); stock schemes fall back to their GUID name, else the error.

## 3. Verdict (`PcVerdict`, pure, selftest fixtures)

| Flag | Rule | Notice |
|---|---|---|
| throttle | > 20% of >= 3 busy samples limited | yes: "Your CPU slows down while playing (heat or power limit) ..." |
| power saver | Power saver scheme or best-power-efficiency overlay | yes |
| battery | `ACLineStatus == 0` | yes |
| affinity | TS3W affinity < logical cores | yes |
| efficiency | efficiency mode on TS3W | yes |
| priority | idle / below-normal priority class | yes |
| other load | other programs >= 25% of all cores | no (summary line) |
| few cores | <= 4 usable threads, or busy game threads + other load + 1 > usable threads: "our worker threads (N) compete with the game; expect smaller gains" | no (summary line) |

Notice: ts3fixes asks op 62 bit by bit at each world load and sim hour and shows ONE notice per game session for the first
problem in the order battery, power saver, efficiency, affinity, priority, throttle, with a one-sentence fix (opt-in: `Notify=1`;
default 0). Proposal only (§4): "fewer route workers on this PC would leave a core for the game" when busy threads + 1 >
logical cores.

## 4. Worker count (proposal, not built)

When the busy-thread count plus one reaches the usable threads, the route / Mono / autonomy workers compete with the sim
thread. A later step could size `[RouteAsync]` workers from the start facts (usable threads minus busy game threads minus
one). Read-only now: the summary prints the proposal line.

## 5. Output

`pccheck:` lines in ts3native.log (start facts; one per sim hour with the CPU verdict, other load, problem bits and hint),
`pccheck-summary.txt` (session block + last-window block, rewritten each hour, at world quit and every 10 min) and
`pccheck.csv` (one row per sim hour) next to the log. `[PcCheck] Enabled=1 Notify=0`: the log part is on by default because it is diagnostic-only (reads Windows numbers, writes only its own log lines and report files, changes nothing in the game, the save or settings); the in-game notice is player-visible, so it is opt-in.

Kit (`tools/player/ts3-kit`, 1120.1): `PcPreflight` before an install prints CPU threads, power plan, battery and free RAM
and warns (never blocks). Player note: COMPATIBILITY.md "Is my PC throttled?".

## 6. Limits

- `CurrentMhz` stays at the base clock on some Intel systems (Windows reports the nominal value); then only `MhzLimit` and
  the "fastest core" rule with a lower max can show a limit. Treat "no throttle seen" as "no limit reported".
- Protected processes that refuse `PROCESS_QUERY_LIMITED_INFORMATION` are missing from the top 3 but are in the total.
- Game Mode is reported, not judged.
