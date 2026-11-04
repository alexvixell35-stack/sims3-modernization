# Harness clock lines (2026-11-03, alexvixell35-stack)

Branch `feat/harness-clock-lines`: `machine-load.ts` rows get `pAvg` / `pMin` (clock read every ~5 s) and `gtop` (top 3 GPU 3D
processes); `profile-world.ts` writes `t0` into `<tag>-ab.json`; `wait-ab.ts` prints the clock per A/B side.

## Measured

- Machine: Lenovo Legion laptop, Ryzen 9 7945HX (2 CCDs, 16C/32T), RTX 4070 Laptop + Radeon 610M, AC power, power plan
  "Legion Performance Mode", TS3W on the dGPU.
- `% Processor Performance` over old-save runs: 109-175%; correlation with GPU 3D load r = -0.55 [measured, machine rows].
- `gtop` sample: `dwm 15`, `TS3W 14`, `csrss 11`: the non-TS3W GPU share is the compositor, so the GPU part of the noise
  rule marks every run on this laptop `medium` [measured].
- Eleven-piece in-load A/B (issue #156, old save, 10 min, 2 min slices): `wait-ab.ts` clock line on 141% vs off 154% (8 / 6
  samples) [measured]. Per-minute rates on the slow side read low by about that much.

## Checks

- `node tools/harness/wait-ab.ts --self-check`: ok (new case: two sides from three rows).
- `node tools/harness/machine-load.ts 40`: PASS, 5 rows with `pAvg`, `pMin`, `gtop`. The default window went from 15 to
  30 s: a row takes ~8 s at the 5 s setting with the clock and GPU reads, and the 15 s check wrote no row once.
