# 2026-11-20: PR #478 `bouncermemo`, imported

By Dalexlol. Contributor data copied from his PR comments (11-19 and the 11-19 22:54 correction); not rerun here.

Evidence (his machine, natural play, switch on, no off arm in the same load): the 11-18 Bridgeport jobs show it acting. Loops skipped moved in 7 games (etl13b to etl14j), 71 to 430 each, all PASS; example etl14h-1118 `af18-bp-31361-n`, 25 min: skipped 430, routed 40. The 11-19 17-job soak (6 worlds, 493.5 min, all PASS) had skipped 0 / routed 0. The earlier hold ("no run on the current base, slots 3422-3423 clash with #518") is answered by the 11-18 runs and the renumbering to slots 1376-1377 (tools/registry.ts, import-478).

Scope: townie bouncers only (`IsNpcSim` = not `IsPlayerSim`, `Household != null`, no parent); household, selected and unknown-scope Sims keep EA's loop. A push of more than 0.25 m routes again, as EA does.
