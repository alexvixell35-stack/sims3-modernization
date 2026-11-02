Thanks, the per-sim-minute view is the right one at max speed, and the corrected off runs were worth the extra time.

The result that matters most for us: autonomy search costs the same per sim-minute on the day-406 save (6.2 ms) as on the young control (6.0 ms). The extra ~35-60% of script time on the old save sits in SimUpdate, `Lot.Simulate`, object queries and NRaas code.

So moving autonomy scoring off the sim thread gains about the same on old saves. Our second offload step (environment score, broadcasters, motives) and the query work should gain more there.

We will put one long save of our own through the same queue, so the absolute numbers on our machine can sit next to yours.

The 4 switches the off arm missed (`visit`, `visitlot`, `gotolot`, `overwatchstuck`) are our fault: the off list should live in one shared place. We will fix that.
