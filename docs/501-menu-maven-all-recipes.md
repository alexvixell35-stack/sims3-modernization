# 501. Menu Maven and Culinary Librarian: "every recipe" counted from the recipes installed (`menumaven`)

The Cooking skill's Menu Maven challenge and the Culinary Librarian lifetime wish both mean "know every recipe". The
game decides both with one fixed number from its tuning, 28. That number was never raised for the expansions, the store
or recipe mods. This doc explains the cause and describes the ts3fixes switch `menumaven`, which counts the target from
the recipes installed in each game. The switch is on by default since 2026-11-14 (Dalexlol PR #153 repro PASS, ours 2026-11-01 PASS). Status (2026-11-01): built, self-tested, forced repro
passed once (§5).

Claim marks: **[verified]** = read in the game's IL by us (`ildasm`; no code reproduced here), in the game's own tuning
files, or seen live; **[inferred]** = our reading, not yet checked in game.

## 1. Cause

- `Cooking.IsMenuMaven()` answers "known recipe count >= `Cooking.kRecipesLearnedForMenuMaven`" [verified].
- Its callers [verified]:
  - the Menu Maven skill challenge (`Cooking.MenuMaven.Completed`);
  - the lifetime wish check `KnowEveryCookingRecipieMajorDreamCheckFunction`;
  - the "learn all recipes" wish checks, `LearnAllRecipiesCheckFunction`, in both the dreams and the challenge delegate
    sets.
- `kRecipesLearnedForMenuMaven` is 28, both in the class constructor and in the game's tuning XML (`GameplayData.package`,
  Steam edition with all packs) [verified].
- The game's recipe list (`RecipeMasterList`, all packs merged) has 144 recipe rows. 64 of them can be learned
  [verified, §2]. A Sim therefore completes the wish knowing 28 of 64 recipes. Store recipes and recipe mods add rows
  that never count either.
- The game already counts installed recipes itself. Every row marked `Learnable` adds 1 to `Recipe.NumLearnableRecipes`
  while the list loads, mod rows included [verified]. Only the skill journal's "recipes known" percentage reads that
  count; the challenge and the wishes do not.
- Today the only fix is to edit the tuning number, and that edit is wrong again whenever a recipe is added or removed.

## 2. Which recipes count

- `Recipe.Learn` is the path for recipe books, the TV cooking show, the restaurant (Natural Cook), level-ups and
  socials. It teaches a recipe only when the recipe is `Learnable`, is not a snack, and its level is at or below the
  Sim's cooking level [verified].
- A recipe counts when it passes those tests at the top cooking level (`Skill.MaxSkillLevel`): `Learnable`, not a snack,
  level at or below the top level (`MenuMavenCore.Counts`). On the test install that is 144 of 432 loaded recipes (§5).
- `Recipe.NumLearnableRecipes` cannot be the target as it is, for two reasons. It counts snacks too when a mod marks them
  `Learnable`. It also counts a row twice when a mod row replaces a recipe that already exists [inferred from the
  loader; on the test install it reads 156 for 144 distinct recipes that count, §5].
- In the game's own list, the 80 rows that do not count are things like snacks, desserts, bar food and vampire food, all
  marked not learnable. 5 learnable rows are pet food; `Recipe.Learn` accepts them, so they count [verified, list read
  with the tuning file's defaults row].

## 3. The fix (`menumaven`, `mods/ts3fixes/src/MenuMaven.cs`)

- **Switch off:** EA's method runs.
- **Switch on:** the Sim must know every recipe that counts.
  - The target is the number of distinct recipe keys in `Recipe.Recipes` that count (§2).
  - The Sim's count is its known recipes that are in that set, each once. A known recipe whose mod was removed does not
    count, which matches the skill journal's rule.
  - The key set is built on the first call and rebuilt when the recipe list or the top level changes. A recipe added
    after load is therefore picked up. Each call costs one pass over the Sim's known list.
  - When no recipe counts (no list yet, or a mod marks none learnable), EA's tuned number decides.
- The tuning number is not used while the switch is on.
- The wish gets harder: on a full install it needs 64 recipes instead of 28, more with store or custom recipes. That is
  the intent of "every recipe" (the player who asked for the fix agreed).
- A Sim who completed the challenge or the wish under EA's rule keeps it. Only new checks use the new rule.
- The rules are in `MenuMavenCore.cs` (self-test `tools/selftest/menumaven`).

| switch | tunable | default | counters |
|---|---|---|---|
| `menumaven` | `kMenuMaven` | on (since 2026-11-14) | 2000 `menumaven_reads` (calls answered by the live rule), 2001 `menumaven_done` (of them true) |

## 4. Checks

- **Self-test** (`node tools/selftest/menumaven/run.ts`): every input of the recipe rule, the done rule over a range, and
  the cases in §2 and §3.
- **Repro** `menumaven-all-recipes-check` (Baseline-BP, queue `tools/harness/queues/menu-maven.json`), using EA's own
  `Cooking.AddRecipe` on the first active-household Sim, teen or older.
  - The Sim's known recipes are saved, emptied, and put back at the end.
  - Off: after adding the tuned number of recipes, `IsMenuMaven` is True.
  - On: with the same recipes it is False. With every recipe that counts it is True. After one is removed it is False.
  - `state` also prints the recipe census (snacks, not learnable, above the top level) and `NumLearnableRecipes`.
- Results: §5.

## 5. Results

Run 2026-11-01, Baseline-BP, one load (job `bp-menumaven-check`, PASS) [verified]. The install has the store and custom
recipe mods of the contributor's game.

- Census: 432 recipes loaded; 31 snacks, 257 not learnable, 0 above the top level; **144 count**.
  `Recipe.NumLearnableRecipes` reads 156, 12 more than the distinct recipes that count (I-900-4).
- Off: 28 recipes added (the tuned number), `IsMenuMaven` True: EA's rule.
- On, same Sim and recipes:

| step | known that count / target | `IsMenuMaven` |
|---|---|---|
| tuned number added | 28 / 144 | False |
| every recipe that counts added | 144 / 144 | True |
| one removed | 143 / 144 | False |

- Counters: `menumaven_reads` 5, `menumaven_done` >= 1. The Sim's own known recipes were put back after each arm.
- Not checked: a Culinary Librarian completion in normal play (the repro calls the check directly).
