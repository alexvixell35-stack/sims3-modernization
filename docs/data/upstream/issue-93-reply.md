Thanks, this is careful work. Write-up: docs/209 §#93.

Both docs/129 corrections are right. We reached the same result independently in docs/208 (closure tool over the 1.67 index): the environment-score events and reactions are interleaved per visited object and Sim, and motive decay writes `Decay`, `EnergyGainRate`, the Frostbitten timeout and the at-work fun delta. docs/129 will point at docs/208.

One addition: docs/208 found a second writing `GetPostureMotiveDecayUpdate` override, `WalkerClassic+InWalkerPosture` (interaction cleanup with yields), next to the baby swing.

New for us and taken into the docs/208 motive design: the pure `EarlyOutMotiveUpdate` skip decision, and your `Curve.Fx` / `m_x` note (we will audit its readers before any decay helper runs on the worker). The G-M5 mod census for the new virtuals is on our list too.

Open question if you have time: did you see any `Curve.X` reader outside the debug/UI code?
