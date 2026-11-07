# fireworksfizzle: a firework that fizzles resets every Sim watching it (2026-11-05)

- Change: `fix/fireworks-fizzle-watch` 8407cd7 (docs/533, issue #166 build 23), ts3fixes built from that branch; native: release v0.2.5 asi (no native change).
- Core: **simler90's UPDATE242 core without my patches** (my core carries build 23); the harness swapped it in for this job only.
- Save: Baseline-BP. Profile: 6 min, seed 12345, repro `fireworks-fizzle-check`, queue `tools/harness/queues/fireworks-fizzle.json`.
- Machine load ok; crashes: none.

| job | result | off arm (`expect broken`) | on arm (`expect fixed`) | `fireworksfizzle_finished` |
|---|---|---|---|---|
| `bp-fireworks-fizzle-check` (run 3) | PASS in 11.2 min | threw `NullReferenceException` (EA's `LaunchFailure` state request on the watch with no state machine), no exit reason | threw nothing, `ExitReason.Finished` added, lastState `ActiveFailure` | 1 (gate >= 1 passed) |

Runs 1 and 2 failed only in the check: run 1 made no firework (a class name, not an instance name); run 2 called the swapped `LoopDel` by reflection, which throws `TargetException` (its header names the hook's class). Run 3 runs the hook's own steps: `FizzleFinish`, else the `LoopOrig` stub.

Not tested: a real fizzle with watchers in play (the `SacsTimeoutException` itself and the reset).
