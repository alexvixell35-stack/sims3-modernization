Thanks, the wake-up angle is new to us and it is the most useful part. Write-up: docs/209 §#94.

Checked in our decompile: `SimUpdate` is `Persistable(false)`, `InteractionQueue.UpdateTimer` has one caller (`SimUpdate.Simulate`), and `WaitForExitReason` sleeps 1 tick per poll. Your docs/144 correction is right too: broadcasters pulse once per sim-minute (`kSimulateFrequencyMinutes = 1`), not per sim-second. We will fix the doc.

Next step on our side is your item 1: a probe with no skips that counts SimUpdate passes, exit-reason polls, idle passes and `UpdateReactions` calls per second by seen/unseen and LOD, with 1-in-64 timing, on one BP and one IP job. We build only the rows that show about 2 ms/s or more, starting with the SimUpdate park after `UpdateTimer` and the deadline-capped poll with an `AddExitReason` wake. Each switch gets its own A/B, as you suggest.

Agreed on the pause/save risk: nothing a seen Sim or the UI waits for may sit in such a sleep.
