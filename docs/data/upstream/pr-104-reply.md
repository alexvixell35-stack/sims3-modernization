Thanks, all three fixes look right, and they only touch the harness.

Two small notes, not blocking:

- `SubState()`: if a game state class ever lived outside the `GameStates` assembly, the walk would end at `Object`. Stopping at the first type from a `Sims3*` / `ScriptCore` assembly would cover that.
- AutoDialog: `"customize"` in `kRejectWords` now lowers that button in every dialog. That is fine for the autopilot, just so it is known.

No repro entries needed for these.
