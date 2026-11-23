# unseen-bands-1122 digest (2026-11-22, docs/322)

Queue unseen-bands-1122 run 2 (chain-unseen-bands-1122b, ts3native 0.36.299-uband2 be3972b9feee, ts3fixes unseen-distance-probe-1122 tuning e267e4a28d61, Bin state apex). bp-bands-smoke PASS 4.1 min, bp-bands PASS 12.4, ip-bands PASS 12.4, sv-bands PASS 12.8; 0 script errors; machine load other CPU mean 7-9% (gate 10%), no reruns. Run 1 (uband1) stopped after its smoke: TaskSeam side table full (957,961 of 1.34M task runs unattributed), no data kept. Per job: `node tools/harness/unseen-band-report.ts <native.json>`, 600 s profile windows.

## unseen-bands-1122-2026-11-22-bp-bands-native.json (600 s)

ubReady 0x7f (loco on, footprint on, plans on, pose on, tasks on, autonomy actors named), edges 50,150,400,1000 m, 600 s, 2441 locomotion passes; autonomy actors named 17948, runs with none 0, camera-only asks 0
| band | Sims/pass | loco ms/s | waits ms/s | footprint ms/s | plans ms/s | pose ms/s | simtask ms/s | autonomy ms/s | total ms/s | simtask runs/s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| <50 m | 2.7 | 1.48 | 0.07 | 0.00 | 0.19 | 0.83 | 6.63 | 2.66 | 11.59 | 74.4 |
| 50-150 m | 8.7 | 4.74 | 0.14 | 0.01 | 0.33 | 2.26 | 18.86 | 5.92 | 31.77 | 179.2 |
| 150-400 m | 15.4 | 9.38 | 0.29 | 0.01 | 0.94 | 2.02 | 36.37 | 9.90 | 57.67 | 381.4 |
| 400-1000 m | 46.4 | 27.56 | 0.38 | 0.04 | 2.92 | 6.95 | 122.54 | 30.41 | 187.46 | 1232.1 |
| >=1000 m | 0.8 | 0.48 | 0.00 | 0.00 | 0.04 | 0.30 | 3.37 | 0.44 | 4.60 | 44.0 |
| household | 8.1 | 5.41 | 0.06 | 0.01 | 0.38 | 9.89 | 21.59 | 7.90 | 44.78 | 70.7 |
| no position | 0.0 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.74 | 0.06 | 0.80 | 0.4 |
NPC total 293.9 ms/s; bands over 5 ms/s: <50 m 11.6 ms/s, 50-150 m 31.8 ms/s, 150-400 m 57.7 ms/s, 400-1000 m 187.5 ms/s
## unseen-bands-1122-2026-11-22-ip-bands-native.json (600 s)

ubReady 0x7f (loco on, footprint on, plans on, pose on, tasks on, autonomy actors named), edges 50,150,400,1000 m, 600 s, 8790 locomotion passes; autonomy actors named 15850, runs with none 0, camera-only asks 0
| band | Sims/pass | loco ms/s | waits ms/s | footprint ms/s | plans ms/s | pose ms/s | simtask ms/s | autonomy ms/s | total ms/s | simtask runs/s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| <50 m | 0.1 | 0.72 | 0.46 | 0.00 | 0.00 | 0.39 | 1.66 | 0.10 | 2.87 | 0.5 |
| 50-150 m | 0.7 | 1.57 | 0.01 | 0.00 | 0.04 | 1.56 | 19.04 | 6.54 | 28.71 | 193.2 |
| 150-400 m | 1.9 | 4.53 | 1.05 | 0.00 | 1.17 | 1.19 | 17.85 | 8.15 | 31.73 | 250.9 |
| 400-1000 m | 15.1 | 25.98 | 1.72 | 0.02 | 10.72 | 5.99 | 149.75 | 36.83 | 218.55 | 1054.8 |
| >=1000 m | 0.0 | 0.03 | 0.00 | 0.00 | 0.00 | 0.02 | 0.42 | 0.01 | 0.47 | 1.6 |
| household | 1.7 | 3.35 | 0.01 | 0.01 | 1.53 | 10.87 | 36.99 | 16.79 | 68.00 | 95.5 |
| no position | 0.0 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.50 | 0.13 | 0.62 | 0.3 |
NPC total 283.0 ms/s; bands over 5 ms/s: 50-150 m 28.7 ms/s, 150-400 m 31.7 ms/s, 400-1000 m 218.6 ms/s
## unseen-bands-1122-2026-11-22-sv-bands-native.json (600 s)

ubReady 0x7f (loco on, footprint on, plans on, pose on, tasks on, autonomy actors named), edges 50,150,400,1000 m, 600 s, 5107 locomotion passes; autonomy actors named 19121, runs with none 1, camera-only asks 0
| band | Sims/pass | loco ms/s | waits ms/s | footprint ms/s | plans ms/s | pose ms/s | simtask ms/s | autonomy ms/s | total ms/s | simtask runs/s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| <50 m | 1.4 | 1.77 | 0.02 | 0.00 | 0.46 | 0.72 | 5.03 | 1.00 | 8.54 | 42.6 |
| 50-150 m | 4.0 | 4.53 | 0.13 | 0.00 | 0.31 | 1.48 | 14.59 | 3.45 | 24.05 | 168.9 |
| 150-400 m | 20.5 | 24.83 | 0.56 | 0.04 | 3.47 | 8.62 | 94.00 | 40.01 | 167.45 | 1424.9 |
| 400-1000 m | 5.7 | 6.12 | 0.09 | 0.00 | 0.53 | 1.24 | 13.53 | 6.21 | 27.10 | 261.5 |
| >=1000 m | 0.0 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.20 | 0.00 | 0.20 | 0.0 |
| household | 3.3 | 5.46 | 0.00 | 0.01 | 0.39 | 9.33 | 33.92 | 74.73 | 123.44 | 87.4 |
| no position | 0.0 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.65 | 0.06 | 0.71 | 0.3 |
NPC total 228.0 ms/s; bands over 5 ms/s: <50 m 8.5 ms/s, 50-150 m 24.0 ms/s, 150-400 m 167.4 ms/s, 400-1000 m 27.1 ms/s
