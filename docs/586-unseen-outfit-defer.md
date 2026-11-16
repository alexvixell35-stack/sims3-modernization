# docs/586 unseenoutfitdefer: instant outfit builds of unseen townies wait until seen

Status: built, default off (ts3fixes switch `unseenoutfitdefer`, tuning `kUnseenOutfitDefer`, issue #451). Natural-play results on 4
worlds below; no in-load A/B yet.

## 1. What it does

A townie's instant outfit change (`Sim.SwitchToOutfitWithoutSpin` and `Sim.RefreshCurrentOutfit`, both ending in
`ScriptCore.CASUtils.CASUtilsSetOutfitInGameObject`) makes the native Sim builder build a model on the main thread. For a Sim nobody
can see, that build is wasted until the Sim is seen. With the switch on, the native call of such a set waits; everything else EA does
(`mPendingOutfitInfo`, `mVisibleOutfitInfo`, the censor, body shape, revealing score, `PostOutfitSwap`, `mPreviousOutfitKey`) still
runs at EA's time. The Sim ends in the state EA's call made, because the native call runs later with the key that was asked for.
`CASUtilsGetOutfitInGameObject` is hooked and answers the waiting key, which is what EA's native would answer [verified: script
assemblies; every script read of a model's outfit goes through it].

## 2. Who is deferred

Household rule (CLAUDE.md): only a live townie that no household controls. `IsPlayerSim` (`PlayerSims.cs`) is the test: a household,
selectable, selected, destroyed, description-less or household-less (unknown scope) Sim is never deferred. Seen is `UfWhy` (the
unseen census's camera rule; on the active lot, near the camera, on screen, in a social with a seen Sim: seen).

Never deferred (EA's call runs at once), counter in brackets:

| case | slot |
|---|---|
| household / selected / unknown-scope Sim, seen townie, not live mode, travelling, saving, world gone | none |
| a Sim's first set after `SimDescription.Instantiate` | 2581 |
| a delayed swap of that Sim is open (between `CASUtilsSetOutfitInGameObjectDelayed` / `SetModelInGameObjectDelayed` and `CompleteDelayedModelSwap` / `AbortDelayedModelSwap`) | 2582 |
| a call that is not a Sim's outfit path (`mPendingOutfitInfo` empty and not `RefreshCurrentOutfit`: robot setup, thumbnails, pie-menu heads) | 2583 |
| the new key's or the shown key's category is not in the safe set (below) | 2583 |
| the Sim is within 40 m of being seen (a camera pan or a mass change must not show old outfits) | 2585 |

Delayed sets (the spin path: `SwitchToOutfitWithSpin`, `SwitchToOutfitWithHiding`, `SwitchToOutfitDelayed`, CAS previews) are never
deferred: they wait for their own ready callback.

## 3. Outfit categories

`Sims3.SimIFace.CAS.OutfitCategories` has Naked, Everyday, Formalwear, Sleepwear, Swimwear, Athletic, Singed, MartialArts, Career,
AuxCareer, Makeover, SkinnyDippingTowel, Racing, Jumping, Bridle, Massage, ChildImagination, Special, Outerwear, Supernatural [verified:
game assembly]. The game has no separate seasonal, weather, party or formal category. Which category a situation picks (a
weather turn, a party, a formal event) is EA's script; this change only looks at the category of the outfit key that script asks for.

- Deferred (both the new and the shown category must be in this set): Everyday, Formalwear, Sleepwear, Swimwear, Athletic, Career,
  Outerwear.
- Passed straight to EA: Naked (the censor follows the managed category, so a late model could show a body with no censor), Singed,
  MartialArts, AuxCareer, Makeover, SkinnyDippingTowel, Racing, Jumping, Bridle, Massage, ChildImagination, Special (costumes and
  towels), Supernatural, None.

## 4. When the waiting outfit goes on

Every entry ends in one of these:

| end | slot |
|---|---|
| the Sim is seen (sweep every 125 ms, up to 128 Sims checked and 12 applied a tick, seen Sims first: a seen Sim goes on within 250 ms) | 2572 |
| the Sim is selected or no longer a townie | 2573 |
| the Sim comes within the 40 m margin | 2586 |
| before a delayed outfit set or delayed model set of that Sim (EA would have built it before that call) | 2574 |
| flush: switch turned off, not live mode (build/buy, CAS, edit town), travelling, or a save (`OptionsModel.SaveGame` hook in `DisposeSpread.cs` applies every waiting outfit before the file is written) | 2575 |
| the Sim is gone (destroyed), or the world quits | 2576 |
| EA's own set of the same Sim runs and replaces it | 2577 |
| a non-delayed whole-model set | 2578 |
| the apply threw (no retry, entry gone) | 2584 |

2570 deferred, 2571 of those replacing a waiting entry, 2579 waiting now, 2580 most waiting, 2587 worst seen-to-apply time in ms (an
upper bound: from the last check that found the Sim unseen), 2588 seen applies later than 250 ms.

A Sim the camera jumps to (portrait click) can show the old outfit for one sweep tick plus the build, then changes as EA's
WithoutSpin changes do (an instant swap).

## 5. Results (author's test PC, Steam 1.67, natural play, switch on in 15 finished games)

Counters at game end [verified: ts3fixes counters]: deferred 237-920 per game, applied when seen 18-325, dropped (Sim gone) 2-321, apply
threw 0 in every game.

Run ji1 (machine clock valid on all 4 worlds) against the mean of runs jh1 / jh2 [verified: frames.csv]:

| world | CAS/TextureCompositor ms/min | frames > 50 ms per min |
|---|---|---|
| Bridgeport | 6890 -> 3944 (-43%) | 92.7 -> 51.6 |
| Isla Paradiso | 4679 -> 2535 (-46%) | 68.2 -> 41.2 |
| Sunset Valley | 4508 -> 3239 (-28%) | 78.7 -> 61.3 |
| Twinbrook | 4825 -> 3386 (-30%) | 71.8 -> 51.1 |

CAS/SimService on Bridgeport: 3306 -> 2158 ms/min. Caveat: those runs had other local switches on, including household variants of
a job-yield switch, so an isolated townie-only build may gain somewhat less; they are not an in-load A/B.
Next test: `node tools/harness/profile-world.ts --tag bp-outfitdefer --ab unseenoutfitdefer --slice 2`.

## 6. Cleanup list (functions read for this port)

- The sweep loop ends when nothing waits and sleeps every pass; at most 12 applies a tick.
- Open and fresh tables are pruned of gone Sims at 2048 entries.
- The round robin moves on by the Sims checked (a tick that stops at its apply cap used to skip Sims).
- A failed apply is counted and dropped, never retried.
- Off: `OdSet` returns at once with nothing waiting; a waiting entry from before the switch went off is applied by the sweep's flush.
