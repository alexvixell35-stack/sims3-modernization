# 851. `[Discourage] DrainGate`: a forced footprint drain behind an NPC job closes the start gate instead of waiting (2026-11-13, contributor, default off)

Code: `native/ts3native/discourage.cpp` (`DgPoll`, `DgOpen`, `DgOpenForJobWait`), one call in `WaitJobDone` (`ts3native.cpp`), selftest block 11d. Counters: own export `ts3native_draingate` (docs/registry.md). Tags: [verified] = log or code; [measured] = in-load A/B numbers; [inferred] = reasoning.

## 1. Problem

Deferred discourage footprint records are applied at drain points. A poll that must drain (the oldest record is at least `kDiscMaxAgeMs` = 250 ms old) while a route job runs makes the sim thread wait for that job: `DiscDrain(force)` takes `+0x918` behind it. On Isla Paradiso these `disc` waits behind unseen / seen-NPC jobs were 53 s of the logged waits >= 300 ms in two 10-minute games (88 waits, up to 2.8 s each) [measured, trace run of 2026-11-13, local build]. The waiting Sim's task could have yielded; nothing needs that job to end first.

## 2. Change

With `DrainGate=1`, `DiscPoll` does not wait when the running job is a townie's (`uf`) or a seen NPC's (`kdScope` 2 or 3): it holds one `g_rgOffSimOpen` count (the worker starts no new job) and returns. The running job ends on its own. The first poll that finds no job applies the records with no wait and opens the gate. The records land after the same job as before, at most one poll later; the sim thread runs with the old footprints meanwhile, as it already does for any record younger than 250 ms [inferred: phase 2's accepted staleness].

- Household, selected and unknown-scope jobs (kdScope 0 / 1; unknown counts as household) keep the forced drain.
- A record older than `kDgHardMs` (3 s) forces the drain anyway (`hard` counter).
- The gate also opens at any completed drain and before a sim-thread wait for one job (`WaitJobDone`: a queued job must be able to start).
- Only `DiscPoll` changes. D1/D2, Usf, Fp2, the reader flush, private footprints, world close and save keep their forced drains.
- Off: `DG_MODE` is 0, `DgPoll` returns false when no gate is held, nothing else changes.

## 3. Evidence [measured]

One in-load A/B, `--ab draingate`, 3 Isla Paradiso games (queue `draingate-1113`, 2026-11-14 01:00-02:00, both start orders), local build: freezes >= 1 s 120.9 (off) -> 97.8 (on) per 10 min (-19%); >= 300 ms 199.3 -> 189.8 (-5%). Old aged Isla Paradiso save (the run predates the 2026-11-14 18:15 save replacement). No crash in these games. The session recorded no verdict and the A/B was not repeated; with 3 games and no repeat the size of the effect is not established.

Deciding test (not run): repeat on the new Isla Paradiso save, 2 games, both orders; keep it only if >= 1 s freezes fall again and the `hard` counter stays near 0.

## 4. Counters

Export `ts3native_draingate`: `mode`, `taken`, `drained`, `hard`, `jobWait`; a "drain gate:" line joins the footprint-removal log line. The harness `--ab` handle of the local build was a word in the old counters block; this PR has none (ini key only).
