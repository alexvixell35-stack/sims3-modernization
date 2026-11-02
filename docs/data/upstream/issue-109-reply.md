Thanks, this goes into the compat matrix as "ok on v0.2.4, autopilot only". We will keep your caveat that custom interactions mostly never ran.

Two findings change our plan:

1. **`[AutShadow]` is off for every session on your install,** because simler90's core mod and tiny-ui-fix both replace `Sims3GameplaySystems` / `Sims3GameplayObjects`. Players with these mods would get no Mono offload gain at all. We will move the mod guard from a whole-session switch to per-method checks before go-live. ErrorTrap's own `ScriptCore` is noted as unchecked.
2. **Apex `0x6F673C`:** we will add a guard at that call site in crashfx as a stopgap and send the site to Apex with the docs/186 report.

The `TimerGuard` analysis is clear, and it shows `easpreadlots` doing its job.

Yes, please do the per-mod A/B for Buzz MoarInteractions, TheRandomizer, Sickness & Health, NRaas Careers and EatOutsideRestaurant. If you can, use the same 4-switch-corrected off arm, and two runs per arm.
