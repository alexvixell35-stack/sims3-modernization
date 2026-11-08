using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Sims3.Gameplay;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Abstracts;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // The fixes (docs/04 F8-F12). Each replaces a game method by copying a hook's MonoMethod header over it before the
    // interpreter compiles it, so they are applied once, at tuning time (Instantiator), before any world loads. A hook
    // calls the original only through an instance stub on its own class that received the original's header.
    // State shared with test tools through AppDomain data (the ts3agent test mod reads and switches it):
    //   ts3fixes.applied (log), ts3fixes.counters (int[]), ts3fixes.switches (name -> bool, overrides the tuning),
    //   ts3fixes.recent (last shortcuts), ts3fixes.moved (every Sim a shortcut moved).
    public static partial class Fixes
    {
        const int kMethodHeader = 40, kNameOffset = 0x18, kSlotOffset = 0x1C;

        static byte[] SwapMethod(MethodBase original, MethodBase replacement, bool keepVirtual = true)  // false: VirtualSwapCheck's old-way pair only
        {
            IntPtr o = original.MethodHandle.Value, r = replacement.MethodHandle.Value;
            byte[] saved = new byte[kMethodHeader], repl = new byte[kMethodHeader];
            Marshal.Copy(o, saved, 0, kMethodHeader);
            Marshal.Copy(r, repl, 0, kMethodHeader);
            if (keepVirtual) KeepVirtual(saved, repl);
            Marshal.Copy(repl, 0, o, kNameOffset);
            Marshal.Copy(repl, kNameOffset + 4, new IntPtr(o.ToInt32() + kNameOffset + 4), kMethodHeader - kNameOffset - 4);
            return saved;
        }

        // docs/536: a virtual original keeps its flags word (+0) and vtable slot (+0x1C bits 12-31). The callvirt resolver
        // (0xE51FC0) reads both from the method a call site names; with the hook's (no VIRTUAL flag) every receiver ran this
        // body and no override did, e.g. every x.Destroy() ran only ScriptObject.Destroy.
        static void KeepVirtual(byte[] saved, byte[] repl)
        {
            if ((BitConverter.ToUInt16(saved, 0) & (ushort)MethodAttributes.Virtual) == 0) return;
            repl[0] = saved[0]; repl[1] = saved[1];
            int slot = (BitConverter.ToInt32(repl, kSlotOffset) & 0xFFF) | (BitConverter.ToInt32(saved, kSlotOffset) & ~0xFFF);
            Array.Copy(BitConverter.GetBytes(slot), 0, repl, kSlotOffset, 4);
        }


        public static void Apply()
        {
            if (AppDomain.CurrentDomain.GetData("ts3fixes.applied") != null) return;
            AppDomain.CurrentDomain.SetData("ts3fixes.applied", new List<object>());
            PatchCounters();
            ApplyTaskCountHook();  // first: the setter must not have run yet
            ApplyAutShadowLocHooks();  // next: any localized string before ApplyAutShadow compiles them (inert while autshadow is off)
            Patch(typeof(GoHome), "Run", typeof(GoHomeHook), "RunHook", "RunOrig");
            Patch(typeof(RabbitHole), "RouteNearEntranceAndEnterRabbitHole", typeof(RabbitHoleHook), "EnterHook", "EnterOrig");
            Patch(typeof(VisitCommunityLot), "Run", typeof(VisitCommunityLotHook), "RunHook", "RunOrig");
            Patch(typeof(VisitLot), "Run", typeof(VisitLotHook), "RunHook", "RunOrig");
            Patch(typeof(GoToLot), "Run", typeof(GoToLotHook), "RunHook", "RunOrig");
            Patch(typeof(GoToCommunityLot), "Run", typeof(GoToCommunityLotHook), "RunHook", "RunOrig");
            ApplyTripSubclasses();
            // No tuning gate here: Apply runs in Instantiator's static constructor, before the tuning loader sets the [Tunable]
            // fields, so every tuning reads its C# default (docs/124 §17.8). Hooks read their tuning per call; patches that must
            // stay out unless a tuning is on go in ApplyTuned.
            Patch(typeof(Sims3.Gameplay.Actors.SimRoutingComponent).GetNestedType("WalkStyleComparer", BindingFlags.NonPublic), "Compare", typeof(WalkStyleCompareHook), "CompareHook", "CompareOrig");
            Patch(typeof(Sims3.Gameplay.Autonomy.ReactionBroadcaster), "PulseSims", typeof(BroadcasterPulseHook), "PulseHook", "PulseOrig");
            ApplyRouteFixes();
            ApplyRouteBackoff();
            ApplyRouteFailLog();  // docs/226: logging only (routefaillog)
            ApplyOverwatchStuck(false);
            ApplyOverwatchSpread(false);
            ApplyOverwatchNotice(false);
            ApplyOverwatchProbe(false);
            ApplyErrorTrapSpread(false);
            ApplyPortalAdaptorLeak();
            ApplyCasGrid();
            ApplySpatialCache();
            ApplyFewerPlans();
            ApplyCandidateLoops();
            ApplyRefTable();
            ApplyCachedReflection();
            ApplyUiCaches();
            ApplyNameCache();
            ApplyScriptHotSpots();
            ApplyVenueMemo();
            ApplyDoorMirror();
            ApplyAutShadow();
            ApplyAutOffload();
            ApplyReplanDefer();
            ApplyUnseenThrottle();
            ApplyEaSpread();
            ApplySeasonMarkerCache();
            ApplyRouteSources();
            ApplyDisposeSpread();
            ApplyMemAlive();
            ApplyUfFinish();
            ApplySavedStacks();  // docs/242
            ApplyPosturePop();
            ApplySeasonImposter();
            ApplyEaFixes();
            ApplyDeadRefs();
            ApplyEaVacation();
            ApplyCompaction();  // docs/204: hooks only with ts3native [Compaction] Enabled=1 or 2
            ApplyCasTrimKeep();  // docs/539: hooks only with [CasTrim] save hooks in native
        }

        // Patches that go in only when a tuning is on (docs/124 §17.8): Apply cannot read tunings (C# defaults there), so these
        // run once the tuning is set: at app startup when the tuning was already parsed (kInstantiator True: every
        // ts3fixes tuning sets it), else at the first world load. Each logs "tuned: <tuning> on: <what> patched|MISSING";
        // queue.ts fails a job on a MISSING line. A target the interpreter already compiled is refused (Patch), never swapped.
        static bool sTunedDone;
        internal static void ApplyTuned(string when)
        {
            if (sTunedDone) return;
            sTunedDone = true;
            AppliedLog().Add("tuned: tuning-gated patches at " + when + (Instantiator.TuningSet ? "" : " (tuning not set: kInstantiator False)"));
            ApplyVenueScore();
            ApplyWakePark();
            ApplyAutLiveB14();
            ApplyHitchCause();
        }

        static void TunedLog(string tuning, bool ok, string what)
        {
            AppliedLog().Add("tuned: " + tuning + " on: " + what + (ok ? " patched" : " MISSING"));
        }

        // Null when target.method exists and is not compiled yet (a Patch would go in), else "Type.method".
        static string NotPatchable(Type target, string method, Type[] args)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo t = target.GetMethod(method, all, null, args, null);
            return t != null && !Compiled(t) ? null : target.Name + "." + method;
        }

        static bool Compiled(MethodInfo t) { return Marshal.ReadInt32(new IntPtr(t.MethodHandle.Value.ToInt32() + 0x14)) != 0; }

        static List<object> AppliedLog() { return (List<object>)AppDomain.CurrentDomain.GetData("ts3fixes.applied"); }

        // Counters, read by test tools: [0-3] go-home runs/shortcuts/no spot/loop guard, [4-6] buildings, [7-18] lot
        // trips (visit, visitlot, gotolot, gotocommunity: runs/shortcuts/no spot each), [21] houseboat placements,
        // [22-23] broadcaster pulses/skipped, [24-26] route backoff, [27-36] route fixes (RouteFixes.cs), [37-39] SafeSpot
        // verdict cache hits/misses/full checks, [40-43] save cleanup (SaveCleanup.cs), [44-45] Overwatch stuck-check
        // plans/yields (OverwatchStuck.cs), [46-51] watchdog idle cases (RouteFixes.cs: idle on an unroutable spot, hidden
        // room, stationary a sim-day, route plans failing, routability checks asked, route-fail Sims tracked), [52] empty life-event lists trimmed, [53] memory snapshots capped (SaveCleanup.cs), [54-55] route
        // adaptor callbacks unsubscribed after DoPortal / stale ones removed by the save cleanup (PortalAdaptorLeak.cs), [56-58] async
        // route plans started/yields/poll cap reached (RouteBackoff.cs, with ts3native [RouteAsync]), [59-61] CAS incremental
        // fills started / hair preset cache hits / preset lookups run (CasGrid.cs), [62-64] lot object query cache hits /
        // misses / clears on object destroy (SpatialCache.cs), [65-67] routability cache hits / plans
        // recorded, stuck checks skipped for busy Sims (FewerPlans.cs), [68-70] save identity tables converted / most entries
        // in one table / table grows (RefTable.cs), [71-73] serializer type caches started / answers from the cache /
        // answers stored (CachedReflection.cs), [74-75] motive bar updates without / with image loads, [76-77] moodlet grid
        // in-place updates / rebuilds (UiCaches.cs), [78-79] Sim name cache hits / misses (NameCache.cs),
        // [80-82] lot detail priority answers from the tick set / tick sets built / verify mismatches,
        // [83-84] cane-walk checks answered early / passed to the game, [85-87] hours-of-operation memo hits / misses / verify mismatches,
        // [90-92] SafeSpot placements answered by a stored FindGoodLocation result / searches run / verify differences (switch safespotreuse),
        // [88-89] tooltip window walks skipped / run (ScriptHotSpots.cs),
        // [93-98] spring wildflower fires / tries / placed / failed / longest ms / save aborts (wildflowerprobe, wildflowertask; ScriptHotSpots.cs, docs/110),
        // [99-101] reserved for invcache (docs/115), [102-105] door mirror: door imports seen / lot invalidations / lot tables built /
        // builds refused (doormirror; DoorMirror.cs, docs/105 §10.5), [106-121] [AutShadow] pilot (AutShadow.cs, docs/122): A0..A5,
        // L pairs / equal / Test agree / top-1 agree, skips, warm-in reference runs, requests posted, Begin busy, mod-guard
        // skips, managed exceptions in the driver, [122-126] [AutShadow] mod guards (docs/128 §5): sf skips, G-M4 methods /
        // us / foreign swaps, G-M5 overriding mod types, [127-131] 3 AM stuck checks in our task (stuckcheckasync; FewerPlans.cs,
        // docs/134 §8): queued / run / dropped / most waiting / longest wait in sim-seconds,
        // [132-136] replan defer / spread (ReplanDefer.cs, docs/141 §6): interrupts inserted, eligible calls kept sync, calls
        // held back, calls replayed, replay exceptions,
        // [137-145] candidate-loop caps (CandidateLoops.cs, docs/141 §7): safe-spot calls capped / limit reached, farthest-lot
        // calls / plans / cache answers / unplanned after the cap, indoor-position calls / plans / rooms given up, [146-155] unseen script throttle
        // (UnseenThrottle.cs, docs/144): environment calls classified / unseen / skipped, motive updates classified / unseen /
        // skipped, broadcaster per-object rule unseen pulses run / skipped, unseen motive updates leaving a need <= -95, gate restarts,
        // [156-166] NRaas Overwatch nightly cleanups spread (OverwatchSpread.cs, docs/145),
        // [167-176] NRaas ErrorTrap load-up inventory check spread (ErrorTrapSpread.cs, docs/146).
        // [177-196] EA maintenance alarms spread (EaSpread.cs, docs/147): venue cleanup passes / lots / Sims stepped / ticks /
        // longest tick us / verify objects left to clean / passes stopped, pet pool alarms moved to our task / pets added / ticks /
        // most ticks for one alarm / alarms left to the game (busy), decay passes / Sims queued / pairs stepped / pairs dropped /
        // ticks / longest tick us / passes finished early / decay steps that threw, [197-200] unseen traffic cars: LiteDriver spot asks / unseen car + spot / turned down (stays parked) / seen (UnseenThrottle.cs, docs/143 §8.5), [201-206] reserved, [207] extra pet pool alarm fires dropped (second chain), [208] most decay pairs stepped in one tick, [209] most venue-cleanup units (objects tested, cleaned or repaired, per-lot steps) in one tick, [210] most Overwatch inventory items checked in one tick (OverwatchSpread.cs), [211-212] ErrorTrap inventory check: largest inventory checked whole (items), most items checked in one tick (ErrorTrapSpread.cs), [213-222] other branches (registry), [223] longest Overwatch cleanup type run whole at its slot (us), [224] Overwatch types that took >= 50 ms at their slot (OverwatchSpread.cs), [225-227] unseen motive catch-ups: most in one sim tick, held a tick by the per-tick cap, longest catch-up call (us) (UnseenThrottle.cs, docs/144 §8), [228-237] route sources and redundant-plan cuts (RouteSources.cs, docs/151 §6): plans classified / classifier us / V3 leg-end replans D1 could skip / V4 houseboat leg plans / V5 bouncer at its slot / V7 watchdog checks a successful plan answered / D1 replans skipped / D3 bouncer routes skipped / D5 checks skipped by the memo / D5 Sims left by the cap; per-bucket data in AppDomain ts3fixes.routesrc (int[64]),
        // [238-243] docs/163 sync-plan callers: stuckchecknear checks run / plans / checks where every point failed (FewerPlans.cs), failrepeat failures recorded / repeats answered / ms of the recorded plans those repeats skipped (RouteBackoff.cs).
        // [251-254] docs/163 §7 owner-task own STARTs: drivertaskasync traffic car / ridden vehicle STARTs sent, stuckcheckown STARTs sent, of those started by ts3native (RouteBackoff.cs).
        // [244-245] overwatchchain: Overwatch alarms run as a chain / ticks yielded between types (OverwatchSpread.cs, docs/145 §10).
        // [255-259] overwatchstuckslice: ticks taken in the Stuck Check's Sim walk / Sims walked / longest stretch without a tick (us) /
        // Stuck Check runs / stretch time summed (ms) (OverwatchStuck.cs, docs/145 §12; measured with the switch off too).
        // [270-275] seasonmarkercache (SeasonMarkerCache.cs, docs/74 §11 (a)): answers from the list / marker queries passed (not eligible) / verify checks / verify mismatches / seeds / markers in the list;
        // [276-278] safespotyield (SafeSpot, docs/74 §11 (b)): yields after a FindGoodLocation / placements given up after a yield (Sim seen or gone) / searches not yielded (no yield context or not the Sim's own task); [279] spare.
        // [280-289] disposespread (DisposeSpread.cs, docs/74 §12): destroys queued / destroyed by our task / destroyed at once by a flush
        // (save, travel, build/buy) / game path taken (unsafe or not live) / most waiting / longest slice us / already destroyed at
        // their turn / queue full (game path) / dropped (world unload, Destroy threw) / longest wait in ticks.
        // [290-292] overwatchstucknotice (OverwatchNotice.cs, docs/145 §13): Overwatch stuck notices held / summaries shown / Sims named.
        // [293-294] owprobeplans (OverwatchProbe.cs, docs/163 §9): EA stuck-check plans hidden from Overwatch's Stuck Check V2 / stuck-check runs flagged.
        // [295-296] preplancb (RouteBackoff.cs, docs/185 §5): AboutToPlanCallback pre-calls before a START / door lists restored after the original plan.
        // [297-301] stuck-check outcomes (FewerPlans.cs StuckCheckRun, docs/163 §10, counted with any switch): [297] stuckcheckcap PROBE marks
        // ts3native accepted, then per check: [298] no plan (moved / early return), [299] a probe succeeded (not stuck), [300] every probe
        // failed before the last round (re-armed), [301] every probe failed in the last round (reset).
        // [302] overwatchstuckslice: longest stretch seen at the IsSelectable hook (us) (OverwatchStuck.cs, docs/145 §12).
        // [303-312] unseen-Sim fallback (UnseenFallback.cs, docs/202): stuck checks deferred / run (seen), plans marked / cut,
        // teleports / refused, no-retry answers, plans seen, marks refused, longest marked plan (ms).
        // [313-318] unseenfbpre (UnseenFallback.cs UfPre, docs/202 §3.5): teleports after a walk-time hold / eligible but no reachability
        // proof (planned normally) / proven unreachable (failed at once) / holds given up (seen, moved, interrupted: planned normally) /
        // holds started / longest hold (sim-minutes); [319-322] unseenfbarrive / unseenfbleave (§3.6): arrive hand-offs / arrive no hand-off
        // or proof, leave teleports / leave no hand-off or proof; [323-325] other branches; [326-327] D paths skipped for a missing
        // verdict (normal plan instead, no sim-thread check) / D cells filled from those normal plans (UfFillNote).
        // [328-340] D / D-arrive / D-leave reject gates (UfPre / UfArrive / UfLeave, docs/202 §3.5.1, shared by the three switches,
        // counted for unseen Sims (D, D-arrive) and seen-but-movable ones (D-leave)): no goal or not the Sim's own task / too short /
        // Sim rules / not a standing unseen townie / car or boat / goal on the Sim's lot / goal lot refused / goal indoors or off the
        // ground / caller not a follow helper (frames in AppDomain ts3fixes.unseenfb.callers) / no yielding context / colour gives no
        // same-component proof / goal not routable or no route profile / D-leave goal in view.
        // [341-347] lot trips with D's hold (docs/202 §3.6.4, unseenfbtrip / unseenfbtriphold): mod subclass trip runs / shortcuts /
        // no safe spot (ShortcutToLot idx 341), trips placed after a walk-time hold (any kind) / holds given up or refused (seen at the
        // start, Sim or spot seen, moved or interrupted: the game's trip runs) / refused: start and lot proven in different flood
        // colours / refused: not the Sim's own task or no yielding context.
        // [348-353] loop detector / breaker (UnseenFallbackLoop.cs, docs/202 §5.7); [354-362] other branches (registry).
        // [363-370] unseenfbfinish (UnseenFallbackFinish.cs, docs/202 §3.4.2): holds claimed / walks finished at the route end /
        // not placed at the end (seen or gone at placement: replanned from where it stood) / holds given up (seen, route changed or
        // ended, not routable: the Sim walks on) / ids flagged finishable / not flagged: route end seen or a household Sim on its lot /
        // not flagged: route shape (not one open walking subpath, slaves, follow-up actions, Sim rules) / longest hold (sim-minutes).
        // [371] UfHeld posture chains that did not end within 16 postures (a loop: held, issue #89, UnseenFallback.cs), [372] posturepopcycle:
        // pops sent down the setter's unwind instead of Compatible (PosturePop.cs, issue #89).
        // [373-379] seasonimposter (SeasonImposter.cs, issue #82): lots queued / warm rebuilds in play / cold rebuilds in play
        // (seasonimpostercold) / rebuilt at a save or travel flush / dropped (lot gone, world unload, threw) / longest rebuild ms /
        // most waiting. [380-390] EA fixes (EaFixes.cs, issues #86-#90): preferredservice extra entries removed / stale entries
        // re-picked / swept at save, socialworkeronlot visits deferred / dropped, eanullguards bribe / topic / motives / carry,
        // showstagethrowables items destroyed, firepitchair chairs destroyed. [391-392] ageupkeepoutfits (EaFixes.cs, issue #100):
        // outfit swaps skipped after a failed build / descriptions with no Naked and no Everyday outfit at the last count.
        // [393-410] dead references (DeadRefs.cs, issue #91): deadrefguards Get refusals / aging states refused; deadrefsweep
        // relationship keys / relationship entries / aging states / alarm owners / react times / coworker-boss-chess / topics /
        // fishers / Sim fields / passenger-horseshoe entries removed, passes finished, slices, longest slice us, total ms,
        // longest save pass us, refused or stopped.
        // [411-422] save cleanup spread pass + savecleanupaggressive (SaveCleanupSpread.cs, docs/14 §8): passes finished, slices,
        // longest slice us, total ms, refused or stopped, outside-household pictures, quiet friend pairs, history entries,
        // dead outfits, dead Sims disposed, cleanse skipped for Overwatch, dead passport lists seen. [423-426] played households
        // (PlayedHouseholds.cs, docs/14 §8.6): set size, ids added, lighter-tier pictures removed, aggressive actions skipped.
        // [427-438] Compaction (Compaction.cs, docs/204): saves seen, compactions started, offers, accepted, declined, refused by state,
        // skipped / failed, queue entries recorded, re-pushed, not pushed, not restorable, pushes that threw; [439] restores finished.
        // [440-447] venue memo (VenueMemo.cs, docs/122 §14.24): GetTuning memo hits, misses, verify mismatches, clears; venue
        // scores by the hook's steps, passed to the game's method, verify mismatches, verify checks.
        // [466-480] free vacation (EaVacation.cs, issue #97, docs/210): vacexport adults exported / dream managers from the export,
        // vacstoreguard slots re-added / stores that threw / bring-backs scheduled at load / dead-pickup trips ended at load,
        // vacreturnskip adults skipped / returns ended, vacteendelete deletions deferred / teens rejoined / returns from the check,
        // vacexitreturn adults brought back at exit, vaclistenernull null states skipped, vacimportfix ids refreshed / expiry skipped.
        // [448-465] offload Mode R (AutOffload.cs, docs/208): env / bcast items, A0-A5, exceptions, dry checks. [481-490] offload
        // apply (docs/208 §8): env ready / applied / no answer / stale / applied = live, bcast ready / applied / no answer / stale / same order.
        // [534-537] autofftick (AutOffload.cs, docs/206 §15): items, A0, A2, unfinished.
        // [538-541] go-live cost guard (AutLive.cs, docs/124 §17.10): windows closed, skip windows, searches skipped, last ratio
        // (cost per mille of the scoring saved; -1 = no reference yet).
        // [550-565] autlivelate (AutLive.cs, docs/217 L1): later slices checked, refused by age/epoch/state/lot/head/actor/other,
        // late objects consumed / stale, consumed by age 1/2/3+ ticks, B14 us consumed / game late / game invalid.
        // [566-574] G8 (AutLive.cs AutPushHook, docs/124 §8): pushes, no-pick searches, MakeSimGoHome fallbacks, Sim-hours x100,
        // request-to-push latency 0/1/2/3/4+ ticks (Mode 3 searches).
        // [575-601] go-live per-search feature mask (AutLive.cs, AutLiveCore.Feat bits late, prelearn, tierreqlots, rain,
        // desirefn, satb, guard, cold, partial): 575-583 searches per bit, 584-592 mismatches (dry unequal or replay mismatch),
        // 593-601 give-ups (live-drop fallbacks, dry-nocmp).
        // [491-533] unseen wake-ups (WakePark.cs, docs/211: wakeprobe / wakepark / wakepoll): see the header of WakePark.cs.
        // [602-604] [AutShadow] G-M6 (AutShadow.cs ClosureSweep, PR #112): closure methods IL-hashed, hashes not from a checked
        // build (owners whose closure reaches it skipped), hashing us. [605] G-M6 guard faults (> 3 mismatches on checked-build MVIDs: IL check ignored). [606] G-M6 owners (definitions + implementors) skipped this load.
        // [641-646] unseenroute (UnseenFallback.cs UfPre relaxed / UfSnap, docs/202 §3.9): relaxed acts (placed at the goal after
        // the hold), acts without a verdict, acts with the colour unknown, holds shortened by the fast-leg estimate, snap-backs placed
        // mid-trip (exact plan from there), snap-backs that planned from where the Sim stood.
        // [647-658] unseenroute2 (UnseenFallback.cs, docs/202 §3.9.5-§3.9.7): acts of plans made in another task, plans refused
        // inside another plan's callback, group acts (members placed with the leader), groups refused (a member seen, held or
        // not human; or a member's own plan), child acts, B passes (street / public lot, same colour), C learn plans (real plan,
        // result cached), C cached hits, D checks (a placed Sim seen again), D repairs (moved to the lot entry), ghost starts
        // (ts3native [UnseenGhost] took the id, footprint removed), ghost exits (off the native set, footprint back).
        // [659-660] unseen-direct pacing (AutLive.cs PaceCharge, docs/222 §8): us charged to B13's yield timer, yields brought forward.
        // [661-669] G8 by actor class (AutLive.cs G8Note, docs/222 §8): unseen searches / pushes / no-pick / GoHome (661-664), seen
        // (665-668), unseen Sim-hours x100 (669).
        // [670-679] autunseencmp Dry per-definition compare (AutLive.cs DefCmpNote, docs/222 §9): searches, pairs, worker-fail /
        // game-pass, worker-pass / game-fail, both pass, both fail, late-slice pairs, late-slice mismatches, objects skipped, snapped slices.
        // [680-682] outfitwaitpoll (OutfitWait.cs, issue #115): outfit waits gone stale, minute polls, stale waits that ended.
        // [2006-2007] ageupswap (AgeUpSwap.cs, docs/503): age-up swaps EA refused and moved to Everyday, age-up tasks released with no swap.
        // [2003-2005] outfitwaitpoll stale-wait causes (OutfitWait.cs, issue #115, docs/224 §5): own swap pending, replaced, nothing pending.
        // [2008-2009] outfitwaitpoll (OutfitWait.cs, docs/224 §5): pending-outfit resets while a helper waits: instant switches, delayed swaps by another path.
        // [683-684] bakesaleseller (BakeSale.cs, issue #102): dead-seller reads, orphaned NPC tables given a new clean-up alarm.
        // [685-686] docs/222 §10: unseen-direct pairs with a RabbitHole target sent to the game's C1 (autunseenrh; in Dry the same pairs
        // skipped by autunseencmp), worker-pass / game-fail compare pairs whose worker score is 0 (BestScores drops both).
        // [2002] uniformbackup (UniformBackup.cs, issue #101, docs/502): stale career uniform backups cleared.
        // [2014] nullservice (NullService.cs, docs/504): null elements dropped from Services.AllServices.
        // [2015] rabbitholedoor (RabbitHoleDoor.cs, docs/505): rabbit hole door requests that threw SacsErrorException, skipped.
        // [2026-2028] photosession (PhotoSession.cs, docs/508): expected Sims dropped, sessions ended by the hook, sessions ended at world load.
        // [2029] eggspot (EggSpot.cs, docs/508): egg positions refused because they are off the hunt lot.
        // [2030] graduationtable (GraduationTable.cs, docs/508): graduation waiting-loop ticks that found no table.
        // [2035-2036] jazzparam (JazzParam.cs, issue #166 build 21, docs/511): UpdateJazzParameter NullReferenceExceptions with no posture, caught.
        // [778-784] unseenprobe/unseenpark (UnseenThrottle.cs, docs/245): low-LOD refreshes run, us in those runs, gap histogram.
        // [818-823] unseenpark (UnseenThrottle.cs, docs/243 §9): held, snap-back, exempt, searches unseen / seen, arrivals; [824] most snap releases in one tick (the stagger working), [825] polls of a seen held Sim deferred to its slot.
        // [836-849] unseenboat (UnseenBoat.cs, docs/248): get-in replans seen by UbCountReplan (switch on or off), of those seen, unseen; shortcuts, no spot, refused route, refused Sim, houseboat teleports, owned boat, candidates (transport or houseboat trips), seen or ended during the gate or wait, refused goal, longest wait (sim-minutes), colours unknown.
        // [854-861] scopegap (UnseenFallback.cs UfDriverless, UfSlowPlanLog; counters only): driverless routes (who == null) by Vehicle+IOwnableVehicle / Vehicle not ownable / household or selectable Sim in Occupants / not a Vehicle; plans of Sims in 80 m viewer range only (viewer bit, no on-screen, no reach), of those >= 250 ms, their total ms; driverless total.
        // [850-853] unseenboatstart (docs/248 §10): Sims leaving a houseboat: candidates, shortcuts, no spot, left dirty (must stay 0). Subsets of [845], [839], [840].
        // [2033-2034] buffupdate (BuffUpdate.cs, issue #166 build 20, docs/510): out-of-sync exceptions from UpdateBuffs, caught.
        // [2039-2040] carnoroute (CarNoRoute.cs, issue #166 build 33, docs/513): car drives with no car routing component, returned false.
        // [2031-2032] convturn (ConvTurn.cs, issue #166 build 19, docs/509): stuck conversation turns seen by a scoring call, turns taken.
        // [2041-2042] npcdriverpool (NpcDriverPool.cs, issue #166 build 37, docs/514): bad pooled driver descriptions EA would hand out, dropped.
        // [687-689] routefaillog (RouteFailLog.cs, docs/226): routefail lines, showoff lines, lines dropped by the cap.
        // [760-762] savedstacks (SavedStacks.cs, docs/242): saved methods remapped to stubs, left dropped, stubs with no checksum.
        // [690-743] unseencost (UnseenCost.cs, docs/227): unseen-check calls / top-level / timed / us per site, interop calls,
        // Sim asks vs distinct Sims per tick, the probe's own calibrated cost. [750-757] unseencost sites utfetch + utlookup (docs/227 §9).
        // [2043-2044] bookcarry (BookCarry.cs, docs/515): carry state machines Book.StopUsingBook dropped without an exit, ended.
        // [2037-2038] ageupabort (AgeUpAbort.cs, issue #166 build 32, docs/512): deferred age-up builds aborted during instantiation, caught.
        // [2045] deadrefsweep phase 9 (DeadRefs.cs, issue #166 build 42, docs/516): dartboard entries that were gone (players quit, current player / cheating Sim cleared).
        // [2046] deadrefsweep phase 10 (DeadRefs.cs, issue #166 build 40, docs/517): object use, reference and routing-reference list entries that were gone.
        // [2047] deadrefsweep phase 11 (DeadRefs.cs, issue #166 build 43, docs/519): object owner components whose owner or thief Sim was gone (relinked or dropped).
        // [2048] deadrefsweep phase 12 (DeadRefs.cs, issue #166 build 41, docs/522): ObjectDisposed handlers of relationships nobody holds, removed.
        // [2049] deadrefsweep phase 13 (DeadRefs.cs, issue #166, docs/523): celebrity waiting-to-react entries that were gone, removed.
        // [2050] rewardgiver (RewardGiver.cs, docs/525): owner components SetRewardGiver added in place of EA's NullReferenceException.
        // [2054] leavejobdispose (LeaveJobDispose.cs, docs/529 §2): deferred job leaves done at once for a description being disposed at work.
        // [2053] unused (a bedpartner fix was dropped: simler90's core already guards the partner in BedMultiPart.ClaimOwnership, docs/529 §1).
        // [2051-2052] datingprofile (DatingProfile.cs, AutShadow.cs MetPeek, docs/528): SimHasProfile answers turned false (a record with no profile) / DidSimsMeetOnline calls answered without making a record.
        // [2055-2057] deadrefsweep object walks (DeadRefs.cs, docs/205 §2.3a): GameObject walks shared by phases 10-12, ms in finished
        // walks, walks asked by kSimDestroyed / kSimDescriptionDisposed (the rest are the daily walk).
        // [2058] invnotyet (InvNotYet.cs, issue #166 build 13, docs/531): objects TryAddObjectToInventory destroyed and refused for a Sim with no inventory yet.
        // [2059-2060] metricskillmissing (MetricSkillMissing.cs, issue #166 build 94, docs/532): skill metric titles / descriptions given an empty skill name, values answered Map0 (neutral), for a skill with no static skill.
        // [2061] fireworksfizzle (FireworksFizzle.cs, issue #166 build 23, docs/533): fireworks watchers finished on a fizzle without EA's LaunchFailure state request.
        // [2062-2064] petpoolorphan (PetPoolOrphan.cs, issue #166 build 52, docs/534): pets dropped from a pool with no household and no family links disposed / left (a Sim in the world) / Dispose threw.
        // [2065] reltablesapart (RelTablesApart.cs, issue #166 build 14, docs/535): relationship pairs whose two tables had come apart, rejoined with the other side's relationship.
        // [876-877] bookhand (BookHand.cs, docs/251): books Book.StopUsingBook left in the hand with no carry machine, seen at an EA put-down that would throw (also with the switch off), moved out of the hand by EA's handler.
        // [758-759] asyncjigsync (AsyncJigSync.cs, RouteBackoff.cs AsyncEligible, docs/240): own plans kept sync while the Sim's interaction holds an unplaced SocialJig / of those with the jig only in the linked interaction.
        // [763-768] autofft2 (AutOffload.cs, docs/208 §9.1): items, A0, A2, A4, A5, exceptions
        // [878-883] autofft2ic (AutOffload.cs, docs/126 §8): items, equal, mismatch, aborts, not compared, exceptions
        // [769-776] autofflot (AutOffload.cs, docs/208 §9.2): items, A0, A2, A4, A5, exceptions, dry checks, dry equal
        // [785-817] G8 search queue wait, manager depth, seen path, household, worker wait, search length (AutLive.cs QueueNote, docs/222 §11.8)
        // [867-870] G8 household part of the seen waits: queue n, ticks, worker n, ticks (AutLive.cs QueueNote, WorkerWait)
        // [898-902] tier-2 H7-H10 (AutShadow.cs, docs/126 §3; autt2drag/autt2cache/autt2tuning/autt2lod): H8 `cache` aborts, H9 `tuning` aborts, H7 drag answers, H10 LOD answers, H10 answers past the game's 60-tick recompute.
        // [903-927] autt2l0 (AutLocalProbe.cs, docs/data/tier2-local-phase-handoff.md L0): local-phase searches, us and pairs by actor class (household, seen, unseen), side entries, no-local floor, unseen B8 reasons, span ms, nested, exceptions
        // [892-897] dancefloorlistdiag (DanceFloorDiag.cs, docs/253 §7): FindAndRouteOntoADanceFloorWithFollower calls, returns, "Collection was modified" throws (all three also with the switch off), route steps checked, steps with a mismatch, of those with the list freed.
        // [928-929] dancefloorlistguard (DanceFloorDiag.cs, docs/253 §11): guarded FindAndRouteOntoADanceFloorWithFollower calls, list changes the snapshot loop got past (EA's next MoveNext would have thrown).
        // [871] watchdoglockskip (RouteFixes.cs CheckIdle, docs/250 §7.4): idle checks skipped while a route job held +0x918 (wdLockSkip)
        static int[] sCountersCache;
        static int[] PatchCounters()
        {
            if (sCountersCache != null) return sCountersCache; // hooks call this hundreds of times a second
            int[] c = AppDomain.CurrentDomain.GetData("ts3fixes.counters") as int[];
            if (c == null) { c = new int[2067]; AppDomain.CurrentDomain.SetData("ts3fixes.counters", c); }
            return sCountersCache = c;
        }

        // [AutShadow] Unseen=1 (UserDiag op 8, no icall: with [AutShadow] off ts3native registers none). Asked once.
        static int sIniUnseen = -1;
        static bool IniUnseen()
        {
            if (sIniUnseen < 0)
            {
                try { sIniUnseen = DiagSend(8, 0) ? 1 : 0; }
                catch (Exception) { sIniUnseen = 0; }
            }
            return sIniUnseen == 1;
        }

        // A fix is on when the test switch says so, else per the tuning (Instantiator).
        static Dictionary<string, bool> sSwitches;
        static bool ShortcutOn(string name)
        {
            if (sSwitches == null)
            {
                sSwitches = AppDomain.CurrentDomain.GetData("ts3fixes.switches") as Dictionary<string, bool>;
                if (sSwitches == null) { sSwitches = new Dictionary<string, bool>(); AppDomain.CurrentDomain.SetData("ts3fixes.switches", sSwitches); }
            }
            bool on;
            if (sSwitches.TryGetValue(name, out on)) return on;
            switch (name)
            {
                case "gohome": return Instantiator.kGoHomeShortcut;
                case "rabbithole": return Instantiator.kBuildingTripShortcut;
                case "visit": case "visitlot": case "gotolot": case "gotocommunity": return Instantiator.kLotTripShortcut;
                case "broadcast": return Instantiator.kBroadcasterThrottle;
                case "virtualhome": return Instantiator.kVirtualHomeShortcut;
                case "failreplan": return Instantiator.kNoRouteFailWalkOffscreen;
                case "watchdog": return Instantiator.kStuckWatchdog;
                case "watchdogidle": return Instantiator.kStuckWatchdogIdle;
                case "backoff": return Instantiator.kRouteFailureBackoff;
                case "stations": return Instantiator.kNoStationDetourOffscreen;
                case "portaladaptorleak": return Instantiator.kPortalAdaptorLeak;
                case "casgrid": case "casmemo": return Instantiator.kCasGrid;
                case "spatialcache": return Instantiator.kSpatialCache;
                case "routablecache": return Instantiator.kRoutableCache;
                case "stuckcheckbusy": return Instantiator.kStuckCheckBusy;
                case "stuckcheckasync": return Instantiator.kStuckCheckAsync;
                case "safespotcap": return Instantiator.kSafeSpotCap;
                case "farthestsorted": return Instantiator.kFarthestLotSorted;
                case "indoorcap": return Instantiator.kIndoorPositionCap;
                case "fastreftable": return Instantiator.kFastRefTable;
                case "cachedreflection": return Instantiator.kCachedReflection;
                case "motivebar": return Instantiator.kMotiveBarImageCache;
                case "buffgrid": return Instantiator.kBuffGridInPlace;
                case "namecache": return Instantiator.kSimNameCache;
                case "detailprio": return Instantiator.kDetailPriorityCache;
                case "hoursmemo": return Instantiator.kHoursMemo;
                case "venuememo": return Instantiator.kVenueMemo;
                case "venuememoverify": return false; // verify switch: test runs only
                case "caneearly": return Instantiator.kCaneEarlyOut;
                case "tooltipthrottle": return Instantiator.kTooltipThrottle;
                case "detailprioverify": case "hoursmemoverify": return false; // verify switches: test runs only, no tuning
                case "safespotreuse": return Instantiator.kSafeSpotReuse;
                case "safespotreuseverify": return false; // verify switch: test runs only
                case "wildflowerprobe": return Instantiator.kWildflowerProbe;
                case "wildflowertask": return Instantiator.kWildflowerTask;
                case "doormirror": return Instantiator.kDoorMirror;
                case "autshadow": return Instantiator.kAutShadow || IniUnseen();
                case "autoffenv": case "autoffbcast": case "autoffdry": return false; // docs/208 offload Mode R (AutOffload.cs): test runs only
                // docs/208 §8 apply switches: unset read true here (default branch) while the agent showed them off
                case "autoffenvapply": case "autoffbcastapply": case "autoffnoref": return false;
                case "autofftick": return false; // docs/206 §15 Mode R tick item: test runs only
                case "autofft2": case "autofft2ic": case "autofflot": return false; // docs/208 §9 Mode R items (tier-2 rows, closest lot): test runs only
                case "autt2inuse": case "autt2fire": case "autt2dragwarm": return false; // docs/219 §3.1 tier-2 P1 worker guards + drag pre-warm (AutShadow.cs): test runs only
                case "autt2drag": case "autt2cache": case "autt2tuning": case "autt2lod": return false; // docs/126 §3 tier-2 H7-H10 worker hooks (AutShadow.cs): test runs only
                case "autt2l0": return false;  // tier-2 L0 local-phase probe (AutLocalProbe.cs): measurement only, test runs
                case "autlive": return IniUnseen(); // docs/124 §17 go-live consume ([AutShadow] Mode=3): test runs, or ini Unseen=1
                case "autlivelate": return Instantiator.kAutLiveLate; // docs/217 L1 late consume (only acts with autlive)
                case "autliveguard": return Instantiator.kAutLiveGuard; // docs/124 §17.10 go-live cost guard (only acts with autlive)
                case "autunseenpace": return Instantiator.kAutUnseenPace; // docs/222 §8 unseen-direct pacing (only acts with autunseen)
                case "autunseenrh": return Instantiator.kAutUnseenRabbitHoleGame; // docs/222 §10 rabbit-hole pairs to the game's C1 (only acts with autunseen)
                // docs/222 §11.8 V6 (only act with autunseen): on with ini Unseen=1, as in the gate 2 credit arm (Rodney 2026-11-08:
                // ON in release, gate 2 BP +6.4 / IP +12.2 ms/s); a seen actor (household, selected) runs the game's own search
                case "autseengame": case "autunseenpacecredit": return IniUnseen();
                case "autunseencmp": return Instantiator.kAutUnseenCmp; // docs/222 §9 Dry per-definition compare (only with Dry + autunseen)
                case "autunseen": return IniUnseen(); // docs/222 unseen-direct go-live (only acts with autlive, acting): test runs, or ini Unseen=1
                case "replandefer": return Instantiator.kReplanDefer;
                case "replanspread": return Instantiator.kReplanSpread;
                case "autshadowverify": return false; // verify switch: test runs only
                case "overwatchspread": return Instantiator.kOverwatchSpread;
                case "overwatchchain": return Instantiator.kOverwatchChain;
                case "overwatchspreadverify": case "overwatchspreadfakeupstream": return false; // test runs only
                case "errortrapspread": return Instantiator.kErrorTrapSpread;
                case "errortrapspreadverify": case "errortrapspreadfakeupstream": return false; // test runs only
                case "autledger": return false; // ledger probe switch (docs/51 mono ledger): test runs only
                case "unseenenv": return Instantiator.kUnseenEnvThrottle;
                case "unseenmotives": return Instantiator.kUnseenMotiveThrottle;
                case "unseenpark": return Instantiator.kUnseenPark;
                case "unseenbroadcast": return Instantiator.kUnseenBroadcastThrottle;
                case "unseenprobe": return false; // probe switch: test runs only
                case "wakeprobe": return false; // probe switch: test runs only (WakePark.cs, docs/211)
                case "unseencost": return false; // probe switch: test runs only (UnseenCost.cs, docs/227)
                case "unseenposcache": return true; // perf cache (UnseenTable.cs, docs/227 §7): on since v0.2.5 with [UnseenTable]
                case "unseentablecopy": return false; // A/B baseline (UnseenTable.cs, docs/227 §9): on = the per-tick copy of every row
                case "wakepark": return Instantiator.kWakePark;
                case "wakepoll": return Instantiator.kWakePoll;
                case "easpreadlots": return Instantiator.kEaSpreadLots;
                case "easpreadpets": return Instantiator.kEaSpreadPets;
                case "easpreaddecay": return Instantiator.kEaSpreadDecay;
                case "easpreadlotsverify": case "easpreaddecayverify": return false; // verify switches: test runs only
                case "unseencars": return Instantiator.kUnseenCarThrottle;
                case "routesrc": return Instantiator.kRouteSources;
                case "watchdoglockskip": return false;  // docs/247 §12.4, docs/250 §7.4: runtime only, default off
                case "alarmtime": case "boatreplanlog": case "owresettime": return false; // docs/247 §9: logging, hooks only with kHitchCauseHooks
                case "legreplanskip": return Instantiator.kLegReplanSkip;
                case "bouncerskip": return Instantiator.kBouncerSlotSkip;
                case "idlememo": return Instantiator.kIdlePlanMemo;
                case "stuckchecknear": return Instantiator.kStuckCheckNear;
                case "failrepeat": return Instantiator.kFailRepeat;
                case "jobyield": return Instantiator.kJobYield;
                case "drivertaskasync": return Instantiator.kDriverTaskAsync;
                case "stuckcheckown": return Instantiator.kStuckCheckOwn;
                case "overwatchstuckslice": return Instantiator.kOverwatchStuckSlice;
                case "seasonmarkercache": return Instantiator.kSeasonMarkerCache;
                case "safespotyield": return Instantiator.kSafeSpotYield;
                case "disposespread": return Instantiator.kDisposeSpread;
                case "overwatchstucknotice": return Instantiator.kOverwatchStuckNoticeSummary;
                case "preplancb": return Instantiator.kPrePlanCallback;
                case "stuckcheckcap": return Instantiator.kStuckCheckCap;
                case "owprobeplans": return Instantiator.kOverwatchIgnoreStuckProbes;
                case "unseenfbstuck": return Instantiator.kUnseenFallbackStuck;
                case "unseenfbplan": return Instantiator.kUnseenFallbackPlan;
                case "unseenfbteleport": return Instantiator.kUnseenFallbackTeleport;
                case "unseenfbsites": return Instantiator.kUnseenFallbackSites;
                case "unseenfbpre": return Instantiator.kUnseenFallbackPre;
                case "unseenfbarrive": return Instantiator.kUnseenFallbackArrive;
                case "unseenfbleave": return Instantiator.kUnseenFallbackLeave;
                case "unseenfbtrip": return Instantiator.kUnseenFallbackTrip;
                case "unseenfbtriphold": return Instantiator.kUnseenFallbackTripHold;
                case "unseenfbloopdet": return Instantiator.kUnseenFallbackLoopDetect;
                case "goalhhexempt": return Instantiator.kGoalHouseholdExempt;
                case "unseenfbloopbreak": return Instantiator.kUnseenFallbackLoopBreak;
                case "unseenfbfinish": return Instantiator.kUnseenFallbackFinish;
                case "unseenroute": return Instantiator.kUnseenRouteRelaxed;
                case "unseenboat": return Instantiator.kUnseenBoat;
                case "unseenboatstart": return Instantiator.kUnseenBoatStart;
                case "posturepopcycle": return Instantiator.kPosturePopCycle;
                case "seasonimposter": return Instantiator.kSeasonImposter;
                case "seasonimpostercold": return Instantiator.kSeasonImposterCold;
                case "preferredservice": return Instantiator.kPreferredService;
                case "socialworkeronlot": return Instantiator.kSocialWorkerOnLot;
                case "eanullguards": return Instantiator.kEaNullGuards;
                case "showstagethrowables": return Instantiator.kShowStageThrowables;
                case "firepitchair": return Instantiator.kFirePitChair;
                case "ageupkeepoutfits": return Instantiator.kAgeUpKeepOutfits;
                case "outfitwaitpoll": return Instantiator.kOutfitWaitPoll;
                case "uniformbackup": return Instantiator.kUniformBackup;
                case "deadrefguards": return Instantiator.kDeadRefGuards;
                case "bakesaleseller": return Instantiator.kBakeSaleSeller;
                case "nullservice": return Instantiator.kNullService;
                case "rabbitholedoor": return Instantiator.kRabbitHoleDoor;
                case "photosession": return Instantiator.kPhotoSession;
                case "eggspot": return Instantiator.kEggSpot;
                case "graduationtable": return Instantiator.kGraduationTable;
                case "jazzparam": return Instantiator.kJazzParam;
                case "buffupdate": return Instantiator.kBuffUpdate;
                case "carnoroute": return Instantiator.kCarNoRoute;
                case "convturn": return Instantiator.kConvTurn;
                case "npcdriverpool": return Instantiator.kNpcDriverPool;
                case "bookcarry": return Instantiator.kBookCarry;
                case "ageupabort": return Instantiator.kAgeUpAbort;
                case "rewardgiver": return Instantiator.kRewardGiver;
                case "datingprofile": return Instantiator.kDatingProfile;
                case "leavejobdispose": return Instantiator.kLeaveJobDispose;
                case "invnotyet": return Instantiator.kInvNotYet;
                case "metricskillmissing": return Instantiator.kMetricSkillMissing;
                case "fireworksfizzle": return Instantiator.kFireworksFizzle;
                case "bookhand": return Instantiator.kBookHand;
                case "bookhanddiag": return Instantiator.kBookHandDiag;
                case "petpoolorphan": return Instantiator.kPetPoolOrphan;
                case "reltablesapart": return Instantiator.kRelTablesApart;
                case "ageupswap": return Instantiator.kAgeUpSwap;
                case "asyncjigsync": return Instantiator.kAsyncJigSync;
                case "deadrefsweep": return Instantiator.kDeadRefSweep;
                case "savecleanupaggressive": return Instantiator.kSaveCleanupAggressive;
                case "vacexport": return Instantiator.kVacExport;
                case "vacstoreguard": return Instantiator.kVacStoreGuard;
                case "vacreturnskip": return Instantiator.kVacReturnSkip;
                case "vacteendelete": return Instantiator.kVacTeenDelete;
                case "vacexitreturn": return Instantiator.kVacExitReturn;
                case "vaclistenernull": return Instantiator.kVacListenerNull;
                case "vacimportfix": return Instantiator.kVacImportFix;
                case "menumaven": return Instantiator.kMenuMaven;
                case "dancefloorlistdiag": return false;  // diagnostics only (DanceFloorDiag.cs, docs/253 §7)
                case "dancefloorlistguard": return false;  // EA bug fix, OFF until the dancefloor-diag stress run names the cause (DanceFloorDiag.cs, docs/253 §12)
                case "compactionfail": return false;  // test only (Compaction.cs, docs/204 §6): the reload throws after the screen is up
                case "spotspread": return true;
                case "routefaillog": return true;  // ON in release (Rodney 2026-11-08): helps triage player reports in early releases
                default: return true;
            }
        }

        static void Patch(Type target, string method, Type hook, string replacement, string stub)
        {
            Patch(target, method, null, hook, replacement, stub);
        }

        // args picks one overload of target.method (null when the name is unique). True when the patch went in.
        static bool Patch(Type target, string method, Type[] args, Type hook, string replacement, string stub)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            try
            {
                MethodInfo t = args == null ? target.GetMethod(method, all) : target.GetMethod(method, all, null, args, null), r = hook.GetMethod(replacement, all);
                if (Compiled(t)) throw new InvalidOperationException("already compiled");
                string swName = AutShadowCore.MemberName(t);  // G-M6: callers' IL hashes name t as before the swap
                if (swName != null) AutShadowCore.SwapNames[t.MethodHandle.Value] = swName;
                if (stub != null) { MethodInfo s = hook.GetMethod(stub, all); SwapMethod(s, t); StubDirect(s); }
                SwapMethod(t, r);
                sStubs.Add(t);  // docs/242: a frame in the hook's body saves as the hook's class, which often cannot find it again
                sPatched[t.MethodHandle.Value] = hook;  // docs/128 G-M4: our own swap, accepted by the closure sweep (by MonoMethod: GetMethods hands out other MethodInfo objects)
                AppliedLog().Add(target.Name + "." + method + ": ok");
                return true;
            }
            catch (Exception e) { AppliedLog().Add(target.Name + "." + method + ": " + e.Message); return false; }
        }

        // Off-screen go-home (PLAN fix b): a townie whose trip home nobody can see skips the walk/drive and appears on its
        // home lot. Only the route is replaced: the game's own lead-in (carpool wait, leaving the visit and group) runs, and
        // the success bookkeeping matches a real arrival, so whatever the Sim does next is unchanged. Anything not clearly
        // safe runs the original. Counters: [0] runs, [1] shortcuts, [2] no safe spot on the lot, [3] loop guard.
        public class GoHomeHook : GoHome
        {
            static int[] sCounters;
            public bool RunHook()
            {
                if (sCounters == null) sCounters = PatchCounters();
                sCounters[0]++;
                if (!ShortcutOn("gohome") || !Unseen(Actor, Target)) return RunOrig();
                // a Sim shortcut twice within 10 sim-minutes is in some loop the game drives: let it walk
                ulong id = Actor.SimDescription.SimDescriptionId;
                long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
                long last;
                if (sLastShortcut.TryGetValue(id, out last) && now - last < kLoopTicks)
                {
                    sCounters[3]++;
                    return RunOrig();
                }
                Sims3.SimIFace.Vector3 pos, fwd;
                if (!SafeSpot(Actor, Target, Target.GetRandomPosition(false, true), out pos, out fwd))
                {
                    sCounters[2]++;
                    return RunOrig();
                }
                // docs/202 §3.6.4 unseenfbtriphold: D's walk-time hold first; given up = the game's own trip
                if (UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")) == 2 && !UfTripHold(Actor, Target, pos)) return RunOrig();
                sLastShortcut[id] = now;
                ShortcutLog(Actor.FullName + " -> " + Target.Name + " from " + (Actor.LotCurrent != null ? Actor.LotCurrent.Name : "street"));
                // same lead-in as GoHome.Run
                if (!Sims3.Gameplay.Objects.Vehicles.CarpoolManager.WaitForCarpool(Actor)) return false;
                Sims3.Gameplay.Situations.VisitSituation.OnSimLeavingLot(Actor);
                Sims3.Gameplay.Situations.GroupingSituation.RemoveSimFromGroupSituation(Actor);
                PlaceSim(Actor, pos, fwd, "home " + Target.Name);
                sCounters[1]++;
                NumSuccess++;
                BeginCommodityUpdates();
                EndCommodityUpdates(true);
                return true;
            }
            public bool RunOrig() { return false; } // receives GoHome.Run's original header
            static readonly Dictionary<ulong, long> sLastShortcut = new Dictionary<ulong, long>();
            static readonly long kLoopTicks = Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(10f, Sims3.Gameplay.Utilities.TimeUnit.Minutes);

            // Townie, alone, standing, going to its own ordinary lot, and neither it nor that lot near the camera or the
            // active household. Houseboats and apartment shells keep their own arrival code.
            bool Unseen(Sims3.Gameplay.Actors.Sim sim, Lot home)
            {
                long t = UcBegin(kUcGoHome, sim != null ? sim.ObjectId.Value : 0);  // docs/227 probe
                bool r = UnseenBody(sim, home);
                UcEnd(kUcGoHome, t);
                return r;
            }
            bool UnseenBody(Sims3.Gameplay.Actors.Sim sim, Lot home)
            {
                if (home == null || !TownieUnseen(sim, SimFollowers)) return false;
                // GoHome.Run may add the household horse as a follower after this point; keep pet households on foot
                if (sim.Household != null && sim.Household.Pets.Count > 0) return false;
                if (sim.LotHome != home || sim.LotCurrent == home || home == Household.ActiveHouseholdLot) return false;
                if (!HouseboatOk(home) || home.BuildableShells.Length > 0) return false;
                return FarFromViewers(home.Position);
            }
        }

        // Outside the camera's reach (a radius that grows as the camera zooms out) and not near any active-household Sim.
        static bool FarFromViewers(Sims3.SimIFace.Vector3 p)
        {
            long t = UcBegin(kUcFar, 0);  // docs/227 probe
            bool r = FarFromViewersBody(p);
            UcEnd(kUcFar, t);
            return r;
        }
        // docs/227 probe run 2 (bp-uc-probe): 3.55 us a call. Now squared distances in plain floats (Vector3 operator- and
        // Length() are calls, Length a double sqrt), the tick test inline (RefreshViewers only on a new tick), and a per-tick
        // answer cache by the exact position (the answer only depends on the point and this tick's viewers: same answer, up to
        // float rounding exactly at the 80 m / reach edge). Broadcasters, lots and doors ask the same points many times a tick.
        static bool FarFromViewersBody(Sims3.SimIFace.Vector3 p)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sViewTick) RefreshViewers(now);
            bool r;
            uint bx = Bits(p.x), by = Bits(p.y), bz = Bits(p.z);
            if (sFarCache.Get(now, bx, by, bz, 0, out r)) return r;
            r = FarCompute(p.x, p.y, p.z);
            sFarCache.Put(now, bx, by, bz, 0, r);
            return r;
        }
        static bool FarCompute(float x, float y, float z)
        {
            float dx = x - sCamTarget.x, dy = y - sCamTarget.y, dz = z - sCamTarget.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            dx = x - sCamPos.x; dy = y - sCamPos.y; dz = z - sCamPos.z;
            if (dx * dx + dy * dy + dz * dz < sReach2) return false;
            const float kMin2 = kShortcutMinDistance * kShortcutMinDistance;
            for (int i = 0; i < sViewerCount; i++)
            {
                dx = x - sViewers[i].x; dy = y - sViewers[i].y; dz = z - sViewers[i].z;
                if (dx * dx + dy * dy + dz * dz < kMin2) return false;
            }
            return true;
        }
        static readonly UcPosCache sFarCache = new UcPosCache();

        // Camera and active-household positions, read once per sim tick (the broadcaster hook alone asks ~800 times/s).
        static long sViewTick = -1;
        static Sims3.SimIFace.Vector3 sCamTarget, sCamPos;
        static float sReach, sReach2;
        static Sims3.SimIFace.Vector3[] sViewers = new Sims3.SimIFace.Vector3[16];
        static int sViewerCount;
        // the caller tests the tick (probe run 2: 1373 calls/s at 0.77 us, almost all the same-tick early return plus the probe
        // pair around it); the refresh site now times only the real reads
        static void RefreshViewers(long now)
        {
            long t = UcBegin(kUcRefresh, 0);  // docs/227 probe
            RefreshViewersBody(now);
            UcEnd(kUcRefresh, t);
        }
        static void RefreshViewersBody(long now)
        {
            sViewTick = now;
            UcCount(kUcViewRead);
            sCamTarget = CameraController.GetTarget();
            sCamPos = CameraController.GetPosition();
            sReach = Math.Max(kShortcutMinDistance, 2f * Math.Abs(sCamPos.y - sCamTarget.y));
            sReach2 = sReach * sReach;
            sViewerCount = 0;
            if (Household.ActiveHousehold == null) return;
            foreach (Sims3.Gameplay.Actors.Sim s in Household.ActiveHousehold.AllActors)
            {
                if (s == null) continue;
                if (sViewerCount == sViewers.Length) Array.Resize(ref sViewers, sViewers.Length * 2);
                sViewers[sViewerCount++] = s.Position;
            }
        }

        const float kShortcutMinDistance = 80f;

        // A spot for a shortcut Sim on `lot` near `near`: on the lot (else the game still sees the Sim away) and passing the
        // game's Sim.IsPointInLotSafelyRoutable (a plan from the spot to the lot's corners and edge midpoints), so the Sim can
        // walk on from there instead of standing stuck. That check is a full route plan (362 ms once in Bridgeport, docs/04
        // F18), so its verdict is cached per 0.5 m cell and route profile (SpotVerdict). Spots next to the lot's verified ones are tried first,
        // rotating through them so arrivals spread out; then fresh spots from `near` (the entrance), whose cells may cost a
        // full check within the per-tick budget. No verified spot = false, and the caller runs the game's own trip.
        static bool SafeSpot(Sims3.Gameplay.Actors.Sim sim, Lot lot, Sims3.SimIFace.Vector3 near, out Sims3.SimIFace.Vector3 pos, out Sims3.SimIFace.Vector3 fwd)
        {
            SpotVerdicts v = Verdicts(lot);
            RouteProfile prof = ProfileOf(sim);
            // "spotspread" off = the earlier build: always start from the lot's last verified spot (A/B for the Bridgeport slowdown)
            bool spread = ShortcutOn("spotspread");
            bool reuse = ShortcutOn("safespotreuse");
            int[] c = PatchCounters();
            for (int i = 0; i < 2 + 3; i++)
            {
                bool fresh = i >= 2;
                if (!fresh && v.Good.Count == 0) continue;
                Sims3.SimIFace.Vector3 start = fresh ? near : v.Good[spread ? v.Next++ % v.Good.Count : v.Good.Count - 1];
                // A verified spot is itself an earlier FindGoodLocation result (a native placement search, hundreds of ms
                // in Isla Paradiso at the school run): answer with it and the facing stored then, while its cached verdict
                // (same cell and route profile; the lot's verdicts reset on a move-in, a moved lot or a day) still says
                // routable. A Sim already standing on the spot is not checked: placed Sims walk on at once.
                bool verify = false;
                Sims3.SimIFace.Vector3 sf;
                if (reuse && !fresh && prof.Ok && v.Fwds.TryGetValue(KeyOf(prof, start), out sf)
                    && LotManager.GetLotAtPoint(start) == lot && SpotVerdict(sim, lot, v, prof, start, false, false, false) == true)
                {
                    if (!VerifyNow("safespotreuseverify"))
                    {
                        c[90]++;
                        if (lot.IsHouseboatLot()) c[21]++;
                        pos = start; fwd = sf;
                        return true;
                    }
                    verify = true; // 1 in 8: also search, return the game's answer, count a different spot
                }
                c[91]++;
                bool found = GlobalFunctions.FindGoodLocation(sim, new World.FindGoodLocationParams(start), out pos, out fwd);
                if (ShortcutOn("safespotyield") && !SafeSpotYield(sim, near)) break;
                if (!found) continue;
                if (verify && (pos - start).Length() > 0.5f) c[92]++;
                if (LotManager.GetLotAtPoint(pos) != lot || SpotVerdict(sim, lot, v, prof, pos, fresh, false, true) != true) continue;
                if (reuse && prof.Ok && v.Fwds.Count < 64) v.Fwds[KeyOf(prof, pos)] = fwd;
                if (lot.IsHouseboatLot()) PatchCounters()[21]++;
                return true;
            }
            pos = fwd = Sims3.SimIFace.Vector3.Invalid;
            return false;
        }

        // safespotyield (docs/74 §11 (b)): one native placement search per sim tick instead of up to 5 in one run. Yields
        // only in the Sim's own task (both hook callers, RabbitHoleHook.EnterHook and GoHomeHook.Run, and the lot trips run
        // in its interaction) and where a Sleep is safe (MayYield, RouteBackoff.cs). The Sim has not moved yet, so the yield
        // changes no outcome; after it the Sim must still be there and unseen, else false = the caller runs the game's trip.
        static bool SafeSpotYield(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 near)
        {
            int[] c = PatchCounters();
            if (Simulator.CurrentTask != sim.ObjectId || !MayYield()) { c[278]++; return true; }
            c[276]++;
            Simulator.Sleep(0);
            if (sim.HasBeenDestroyed || !TownieUnseen(sim, null) || !FarFromViewers(near)) { c[277]++; return false; }
            return true;
        }

        // Verdicts of the game's Sim.IsPointInLotSafelyRoutable for one lot (SafeSpot and the stuck watchdog). The check
        // plans with CreateRouteAsAdult(): the Sim's own species/gender flags with the age forced to adult, plus every option
        // SetRouteOptions sets from the Sim (real age, posture, ghost buff, mermaid, lifeguard, UFO, frozen ponds, pet,
        // horse, special-service pass-through, or none at all under an IHasCustomRouteOptions interaction), then water
        // planning = IsHuman; no valid-room limit (the adult flags skip it). So a verdict is keyed by the cell plus that
        // exact route profile (RouteProfile, read back from a route built the same way), never by IsHuman alone. Kept for
        // one sim-day, and dropped when the lot's household changes (a move-in or move-out refurnishes it), the lot moved
        // (a houseboat at another dock) or the clock went back (another save or world was loaded).
        class SpotVerdicts
        {
            public long Created;
            public ulong Household;
            public Sims3.SimIFace.Vector3 LotPos;
            public readonly Dictionary<VerdictKey, bool> Cells = new Dictionary<VerdictKey, bool>(new VerdictKeyComparer());
            public readonly List<Sims3.SimIFace.Vector3> Good = new List<Sims3.SimIFace.Vector3>();
            public readonly Dictionary<VerdictKey, bool> PlanOk = new Dictionary<VerdictKey, bool>(new VerdictKeyComparer()); // docs/202 D only: cells a normal plan proved (UfFillNote)
            public readonly Dictionary<VerdictKey, Sims3.SimIFace.Vector3> Fwds = new Dictionary<VerdictKey, Sims3.SimIFace.Vector3>(new VerdictKeyComparer()); // facing found with a verified spot (switch safespotreuse)
            public int Next;
        }
        static readonly Dictionary<ulong, SpotVerdicts> sSpotVerdicts = new Dictionary<ulong, SpotVerdicts>();
        static readonly long kVerdictTicks = Sims3.Gameplay.Utilities.SimClock.ConvertToTicks(1f, Sims3.Gameplay.Utilities.TimeUnit.Days);
        const int kMaxGoodSpots = 8, kMaxSpotChecksPerTick = 1;
        static long sSpotCheckTick = -1;
        static int sSpotChecks;

        static SpotVerdicts Verdicts(Lot lot)
        {
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            ulong hh = lot.Household != null ? lot.Household.HouseholdId : 0;
            SpotVerdicts v;
            if (!sSpotVerdicts.TryGetValue(lot.LotId, out v) || now < v.Created || now - v.Created > kVerdictTicks || v.Household != hh
                || (lot.Position - v.LotPos).Length() > 0.5f)
            {
                v = new SpotVerdicts();
                v.Created = now;
                v.Household = hh;
                v.LotPos = lot.Position;
                sSpotVerdicts[lot.LotId] = v;
            }
            return v;
        }

        // What the game's check feeds the planner for this Sim right now: the route's age/gender/species flags and every
        // RouteOption/RouteOption2 bit, read back from a route built exactly as IsPointInLotSafelyRoutable builds it. Not
        // cacheable (Ok = false) for a Sim with a parent (a boat route gets the boat as follower and ignore-object, which
        // the option bits do not show; vehicles, beds).
        struct RouteProfile { public bool Ok; public uint Flags; public long Opts, Opts2; }
        static Route.RouteOption[] sRouteOpts;
        static Route.RouteOption2[] sRouteOpts2;
        static RouteProfile ProfileOf(Sims3.Gameplay.Actors.Sim sim)
        {
            RouteProfile p = new RouteProfile();
            if (sim == null || sim.Parent != null || sim.SimRoutingComponent == null || sim.SimDescription == null) return p;
            if (sRouteOpts == null)
            {
                sRouteOpts = (Route.RouteOption[])Enum.GetValues(typeof(Route.RouteOption));
                sRouteOpts2 = (Route.RouteOption2[])Enum.GetValues(typeof(Route.RouteOption2));
            }
            Route r = sim.SimRoutingComponent.CreateRouteAsAdult();
            r.SetOption(Route.RouteOption.EnableWaterPlanning, sim.IsHuman); // as the game's check does next
            foreach (Route.RouteOption o in sRouteOpts) if (o != Route.RouteOption.None && r.GetOption(o)) p.Opts |= (long)o;
            foreach (Route.RouteOption2 o in sRouteOpts2) if (o != Route.RouteOption2.None && r.GetOption2(o)) p.Opts2 |= (long)o;
            p.Flags = (sim.SimDescription.SimFlags & 0xFFFFFF80u) | 0x20u; // CreateRouteAsAdult's flags
            p.Ok = true;
            return p;
        }

        // Cells are 0.5 m and floor-aligned, so a cell never straddles a wall or fence on a tile edge; y in 1 m steps keeps
        // floors apart.
        struct VerdictKey { public long Cell, Opts, Opts2; public uint Flags; }
        class VerdictKeyComparer : IEqualityComparer<VerdictKey> // explicit: the default one boxes structs in this Mono
        {
            public bool Equals(VerdictKey a, VerdictKey b) { return a.Cell == b.Cell && a.Opts == b.Opts && a.Opts2 == b.Opts2 && a.Flags == b.Flags; }
            public int GetHashCode(VerdictKey k) { return k.Cell.GetHashCode() ^ (k.Opts.GetHashCode() * 31) ^ (k.Opts2.GetHashCode() * 17) ^ (int)k.Flags; }
        }

        // The verdict for `pos`: cached when the profile is cacheable, else a full check when `mayCheck` and this sim tick's
        // budget (shared by SafeSpot and the watchdog) allow; null = no verdict now (SafeSpot treats it as unsafe, the
        // watchdog retries next tick). `recheckBad`: a cached "not routable" is re-checked (the watchdog acts on it; a stale
        // bad verdict must not reset a Sim). `addGood`: remember the spot for SafeSpot's spreading.
        // Counters: [37] cache hits, [38] misses, [39] full checks run.
        static VerdictKey KeyOf(RouteProfile p, Sims3.SimIFace.Vector3 pos)
        {
            VerdictKey key = new VerdictKey();
            key.Cell = ((long)((int)Math.Floor(pos.x * 2f) & 0xFFFFF) << 40) | ((long)((int)Math.Floor(pos.z * 2f) & 0xFFFFF) << 20)
                | (long)(uint)((int)Math.Floor(pos.y) & 0xFFFFF);
            key.Opts = p.Opts; key.Opts2 = p.Opts2; key.Flags = p.Flags;
            return key;
        }

        static bool? SpotVerdict(Sims3.Gameplay.Actors.Sim sim, Lot lot, SpotVerdicts v, RouteProfile p, Sims3.SimIFace.Vector3 pos, bool mayCheck, bool recheckBad, bool addGood)
        {
            int[] c = PatchCounters();
            VerdictKey key = KeyOf(p, pos);
            bool ok;
            if (p.Ok && v.Cells.TryGetValue(key, out ok) && (ok || !recheckBad)) { c[37]++; return ok; }
            c[38]++;
            long now = Sims3.Gameplay.Utilities.SimClock.CurrentTicks;
            if (now != sSpotCheckTick) { sSpotCheckTick = now; sSpotChecks = 0; }
            if (!mayCheck || sSpotChecks >= kMaxSpotChecksPerTick) return null;
            sSpotChecks++;
            c[39]++;
            sInOwnCheck = true;
            try { ok = sim.IsPointInLotSafelyRoutable(lot, pos); } finally { sInOwnCheck = false; }
            if (p.Ok) v.Cells[key] = ok;
            if (ok && addGood && p.Ok && v.Good.Count < kMaxGoodSpots) v.Good.Add(pos);
            return ok;
        }

        // Houseboat lots (PLAN fix b2): most of their cost is Sims boarding and leaving through gangways
        // (SimRoutingComponent.CheckAndUpdateForHouseboats, 5-8% in Isla Paradiso), not the boats moving, so trips to a
        // houseboat may be shortcut too, but only while it is docked and not moving.
        static bool HouseboatOk(Lot lot)
        {
            if (!lot.IsHouseboatLot()) return true;
            Sims3.Gameplay.Objects.Island.Houseboat b = lot.GetHouseboat();
            return b != null && b.IsDocked;
        }

        static void PlaceSim(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 pos, Sims3.SimIFace.Vector3 fwd)
        {
            sim.SimRoutingComponent.DisableDynamicFootprint();
            sim.SetPosition(pos);
            sim.SetForward(fwd);
        }

        // A townie on its own, on foot, standing, and not near anything the player can see. Shared by the trip shortcuts.
        static bool TownieUnseen(Sims3.Gameplay.Actors.Sim sim, List<Sims3.Gameplay.Actors.Sim> followers)
        {
            long t = UcBegin(kUcTownie, sim != null ? sim.ObjectId.Value : 0);  // docs/227 probe
            bool r = TownieUnseenBody(sim, followers);
            UcEnd(kUcTownie, t);
            return r;
        }
        static bool TownieUnseenBody(Sims3.Gameplay.Actors.Sim sim, List<Sims3.Gameplay.Actors.Sim> followers)
        {
            if (sim == null || sim.IsSelectable || sim.Household == Household.ActiveHousehold) return false;
            if (followers != null && followers.Count > 0) return false;
            if (sim.Posture != sim.Standing || sim.IsInRidingPosture || sim.IsInBeingRiddenPosture) return false;
            if (Sims3.Gameplay.Controllers.TombRoomManager.IsObjectInATombRoom(sim)) return false;
            int b = UtbBits(sim);  // docs/227 §6 central table
            if (b >= 0) return sUtbCheck ? UtbCompare(UtbUnseen(b), !UcOnScreen(sim) && FarFromViewers(sim.Position)) : UtbUnseen(b);
            if (UcOnScreen(sim)) return false;
            return FarFromViewers(sim.Position);
        }

        static void ShortcutLog(string line)
        {
            List<object> log = RecentLog();
            if (log.Count >= 40) log.RemoveAt(0);
            log.Add(line);
        }

        // Every Sim a shortcut moved this session (name -> last move and where it was placed), so a stuck Sim found
        // later can be matched for certain; the 40-line log above only covers the last few seconds.
        static Dictionary<string, object> MovedSims()
        {
            Dictionary<string, object> d = AppDomain.CurrentDomain.GetData("ts3fixes.moved") as Dictionary<string, object>;
            if (d == null) { d = new Dictionary<string, object>(); AppDomain.CurrentDomain.SetData("ts3fixes.moved", d); }
            return d;
        }

        static void PlaceSim(Sims3.Gameplay.Actors.Sim sim, Sims3.SimIFace.Vector3 pos, Sims3.SimIFace.Vector3 fwd, string what)
        {
            PlaceSim(sim, pos, fwd);
            MovedSims()[sim.FullName] = what + " @" + Math.Round(pos.x, 1) + "," + Math.Round(pos.z, 1) + " " + Sims3.Gameplay.Utilities.SimClock.CurrentTime().ToString();
        }

        // Trips into buildings (PLAN fix b3): work, school, shops, restaurants. An unseen townie heading to an unseen rabbit
        // hole far away is placed at a safe spot by the entrance, then the game's own method runs: it plans only the last
        // few metres and does everything after the walk unchanged (entering animation, success count, followers).
        // Counters: [4] calls, [5] shortcuts, [6] no safe spot.
        public class RabbitHoleHook : Sims3.Gameplay.Abstracts.RabbitHole
        {
            static int[] sCounters;
            public override RabbitHoleTuningClass RabbitHoleTuning { get { return null; } } // never instantiated; required by the base class
            public bool EnterHook(Sims3.Gameplay.Actors.Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            {
                if (sCounters == null) sCounters = PatchCounters();
                sCounters[4]++;
                Sims3.Gameplay.Abstracts.RabbitHole proxy = RabbitHoleProxy;
                if (ShortcutOn("rabbithole") && proxy != null && proxy.EnterSlots.Count > 0 && TownieUnseen(a, inst != null ? inst.SimFollowers : null))
                {
                    Lot lot = proxy.LotCurrent;
                    Sims3.SimIFace.Vector3 door = proxy.GetPositionOfSlot(proxy.EnterSlots[0]);
                    if (lot != null && !lot.IsWorldLot && a.LotCurrent != lot && (a.Position - door).Length() > kShortcutMinDistance
                        && !UcOnScreen(proxy) && FarFromViewers(door))
                    {
                        Sims3.SimIFace.Vector3 pos, fwd;
                        if (SafeSpot(a, lot, door, out pos, out fwd)
                            && (UfPreCore.TripMode(false, false, ShortcutOn("unseenfbtriphold")) != 2 || UfTripHold(a, lot, pos)))  // docs/202 §3.6.4
                        {
                            string from = a.LotCurrent != null ? a.LotCurrent.Name : "street";
                            PlaceSim(a, pos, fwd, "building " + proxy.CatalogName);
                            sCounters[5]++;
                            ShortcutLog(a.FullName + " => " + proxy.CatalogName + " from " + from);
                        }
                        else sCounters[6]++;
                    }
                }
                return EnterOrig(a, inst, beforeEntering, canUseCar, routeMetaType, playRouteFailure);
            }
            public bool EnterOrig(Sims3.Gameplay.Actors.Sim a, IRabbitHoleFollowers inst, BeforeEnteringRabbitHoleDelegate beforeEntering, bool canUseCar, Route.RouteMetaType routeMetaType, bool playRouteFailure)
            { return false; } // receives RabbitHole.RouteNearEntranceAndEnterRabbitHole's original header
        }

        // Shared by the lot-trip shortcuts: counts a call in [idx], and if the townie and the lot are both unseen and far
        // apart, places the townie on a safe spot on the lot ([idx+1]) or gives up ([idx+2], no safe spot). Lots with their
        // own arrival code (moving houseboats, apartment shells, resorts, diving lots, the active household's lot) are left alone,
        // and so are townies whose household has pets (the game may add a pet or horse as a follower after the move, which
        // would then start far away).
        // newKind (docs/202 §3.6.4): a trip kind the shortcuts above miss (a mod subclass overriding Run); with D's walk-time hold
        // (UfTripHold, in transit) before the move, as covered kinds get with unseenfbtriphold.
        static bool ShortcutToLot(Sims3.Gameplay.Actors.Sim sim, Lot lot, string name, int idx) { return ShortcutToLot(sim, lot, name, idx, false); }
        static bool ShortcutToLot(Sims3.Gameplay.Actors.Sim sim, Lot lot, string name, int idx, bool newKind)
        {
            int[] c = PatchCounters();
            c[idx]++;
            int mode = UfPreCore.TripMode(newKind, ShortcutOn("unseenfbtrip"), ShortcutOn("unseenfbtriphold"));
            if (mode == 0 || !ShortcutOn(name) || sim == null || lot == null || lot.IsWorldLot || sim.LotCurrent == lot) return false;
            if (!HouseboatOk(lot) || lot.BuildableShells.Length > 0 || lot == Household.ActiveHouseholdLot) return false;
            if (lot.CommercialLotSubType == CommercialLotSubType.kEP10_Resort || lot.CommercialLotSubType == CommercialLotSubType.kEP10_Diving) return false;
            if (sim.Household != null && sim.Household.Pets.Count > 0) return false;
            if (!TownieUnseen(sim, null) || (sim.Position - lot.Position).Length() <= kShortcutMinDistance || !FarFromViewers(lot.Position)) return false;
            Sims3.SimIFace.Vector3 pos, fwd;
            if (!SafeSpot(sim, lot, lot.EntryPoint(), out pos, out fwd)) { c[idx + 2]++; return false; }
            if (mode == 2 && !UfTripHold(sim, lot, pos)) return false;  // the game's own trip runs (the Sim lost only the hold)
            string from = sim.LotCurrent != null ? sim.LotCurrent.Name : "street";
            PlaceSim(sim, pos, fwd, name + " " + lot.Name);
            c[idx + 1]++;
            ShortcutLog(sim.FullName + " => " + lot.Name + " (" + name + " from " + from + ")");
            return true;
        }

        // Walk-style comparer (PLAN fix a): SimRoutingComponent's private WalkStyleComparer.Compare looks both walk styles up
        // in a Dictionary<WalkStyle, int>, and with the default enum comparer each lookup boxes and calls Enum.Equals and
        // GetHashCode: 5% of script time as Enum.Equals alone in Bridgeport (docs/04 F7). The enum is sequential from 0, so
        // an array indexed by value gives the same priorities; a style missing from the table still throws, as before.
        // Always patched (routing may compile Compare before the first world load); the tuning kWalkStyleComparer is read per
        // call, because Apply runs before the tuning loader sets it (docs/124 §17.8): a player's False now runs the game's code.
        public class WalkStyleCompareHook
        {
            static int[] sPrio;
            public int CompareHook(Sims3.Gameplay.Actors.Sim.WalkStyle x, Sims3.Gameplay.Actors.Sim.WalkStyle y)
            {
                if (!Instantiator.kWalkStyleComparer) return CompareOrig(x, y);
                if (sPrio == null) sPrio = WalkStylePriorities();
                int px = (int)x < sPrio.Length ? sPrio[(int)x] : -1, py = (int)y < sPrio.Length ? sPrio[(int)y] : -1;
                if (px < 0 || py < 0) throw new KeyNotFoundException();
                return py - px;
            }
            public int CompareOrig(Sims3.Gameplay.Actors.Sim.WalkStyle x, Sims3.Gameplay.Actors.Sim.WalkStyle y) { return 0; } // receives Compare's original header

            static int[] WalkStylePriorities()
            {
                Sims3.Gameplay.Actors.Sim.WalkStyle[] order = (Sims3.Gameplay.Actors.Sim.WalkStyle[])typeof(Sims3.Gameplay.Actors.SimRoutingComponent)
                    .GetField("kWalkStylePriorities", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                int max = 0;
                foreach (Sims3.Gameplay.Actors.Sim.WalkStyle w in order) max = Math.Max(max, (int)w);
                int[] p = new int[max + 1];
                for (int i = 0; i < p.Length; i++) p[i] = -1;
                for (int i = 0; i < order.Length; i++) p[(int)order[i]] = i; // later duplicates win, as the dictionary's indexer
                return p;
            }
        }

        // Reaction broadcasters (PLAN fix c): every broadcaster (music, fires, smells, ...) pulses about once per second:
        // a spatial query for Sims in its radius plus line-of-sight checks per Sim (ReactionBroadcaster.PulseSims, 7% of
        // script time in Bridgeport). One far from the camera and the active household now pulses about one call in four
        // (staggered by a shared call counter), so an unseen Sim starts or stops reacting a few sim-seconds later; near the
        // player nothing changes. ReactionBroadcaster has no parameterless constructor, so the hook is a plain class.
        // Counters: [22] pulses, [23] skipped.
        public class BroadcasterPulseHook
        {
            static int sCalls;
            public void PulseHook(float pulseRadius, bool fullSimulation)
            {
                Sims3.Gameplay.Autonomy.ReactionBroadcaster b = (Sims3.Gameplay.Autonomy.ReactionBroadcaster)(object)this;
                int[] c = PatchCounters();
                c[22]++;
                Sims3.Gameplay.Interfaces.IGameObject o = b.BroadcastingObject;
                // docs/144: per-broadcaster one pulse in kUnseenBroadcastEvery, staggered by the broadcaster, instead of the
                // shared 1-in-4 counter below (which can starve the same broadcasters when their pulses line up with it)
                if (o != null && ShortcutOn("unseenbroadcast"))
                {
                    int key = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b);
                    if (FarFromViewers(o.Position))
                    {
                        long ut = UcBegin(kUcGate, 0);  // docs/227 probe
                        bool run = sBroadcastGate.Unseen(key, Math.Max(1, Instantiator.kUnseenBroadcastEvery));
                        UcEnd(kUcGate, ut);
                        NoteGateClears(c);
                        if (!run) { c[23]++; c[153]++; return; }
                        c[152]++;
                    }
                    else sBroadcastGate.Seen(key);
                    PulseOrig(pulseRadius, fullSimulation);
                    return;
                }
                if (ShortcutOn("broadcast") && o != null && (++sCalls & 3) != 0 && FarFromViewers(o.Position))
                {
                    c[23]++;
                    return;
                }
                PulseOrig(pulseRadius, fullSimulation);
            }
            public void PulseOrig(float pulseRadius, bool fullSimulation) { } // receives ReactionBroadcaster.PulseSims' original header
        }

        // Home visits (VisitLot) and plain lot trips (GoToLot, GoToCommunityLot): same as community visits, the game's
        // Run follows with a short route to the door and its own arrival. Counters: visitlot [10-12], gotolot [13-15],
        // gotocommunity [16-18].
        public class VisitLotHook : VisitLot
        {
            public bool RunHook() { ShortcutToLot(Actor, Target, "visitlot", 10); return RunOrig(); }
            public bool RunOrig() { return false; } // receives VisitLot.Run's original header
        }
        public class GoToLotHook : GoToLot
        {
            public bool RunHook()
            {
                Sims3.Gameplay.Actors.Sim sim = Actor as Sims3.Gameplay.Actors.Sim;
                if (sim != null && (Followers == null || Followers.Count == 0)) ShortcutToLot(sim, Target, "gotolot", 13);
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives GoToLot.Run's original header
        }
        public class GoToCommunityLotHook : GoToCommunityLot
        {
            public bool RunHook() { ShortcutToLot(Actor, Target, "gotocommunity", 16); return RunOrig(); }
            public bool RunOrig() { return false; } // receives GoToCommunityLot.Run's original header
        }

        // Visits to community lots (PLAN fix b3): parks, beaches, venues. An unseen townie going alone to an unseen lot far
        // away is placed at a safe spot on that lot, then VisitCommunityLot.Run runs as usual: its route to the front door
        // is now a few metres, and arrival (success count, "do something after arriving") is the game's own. The follower
        // list is preset empty so the random "bring a pet or housemate" pick, whose followers would start far away, is
        // skipped for that trip. Resorts, diving lots, houseboats and apartment shells keep their own code.
        // Counters: [7] runs, [8] shortcuts, [9] no safe spot.
        public class VisitCommunityLotHook : VisitCommunityLot
        {
            public bool RunHook()
            {
                if ((mFollowers == null || mFollowers.Count == 0) && ShortcutToLot(Actor, Target, "visit", 7))
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives VisitCommunityLot.Run's original header
        }

        // docs/202 §3.6.4 (unseenfbtrip): a mod that installs its own VisitCommunityLot subclass as the game's visit singletons and
        // overrides Run skips VisitCommunityLotHook (LynMods University More Shell Entries: VisitCommunityLotLabLecture runs every
        // community visit in this install, SV ~2.2 / IP ~7.6 ms/s of sync plan samples). Its Run gets the same shortcut with D's
        // walk-time hold, only for instances of the current visit singletons (the mod's follower interactions keep their walk).
        // One stub per patched Run (the stub receives that Run's header); more overriding types than slots: logged, left alone.
        public abstract class VclSubBase : VisitCommunityLot
        {
            protected void Shortcut()
            {
                object d = InteractionDefinition;
                if (!ReferenceEquals(d, VisitCommunityLot.Singleton) && !ReferenceEquals(d, VisitCommunityLot.NoAdditionalActionsSingleton)) return;
                if ((mFollowers == null || mFollowers.Count == 0) && ShortcutToLot(Actor, Target, "unseenfbtrip", 341, true))
                    mFollowers = new List<Sims3.Gameplay.Actors.Sim>();
            }
        }
        public class VclSubHook0 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook1 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        public class VclSubHook2 : VclSubBase { public bool RunHook() { Shortcut(); return RunOrig(); } public bool RunOrig() { return false; } }
        static readonly Type[] kVclSubHooks = { typeof(VclSubHook0), typeof(VclSubHook1), typeof(VclSubHook2) };
        static readonly List<Type> sVclSubPatched = new List<Type>();

        // at tuning time and again at world load (script mods may load after us); Run must not be compiled yet (Patch checks)
        internal static void ApplyTripSubclasses()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            string ours = typeof(Fixes).Assembly.GetName().Name;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!UfPreCore.TripScanAssembly(a.GetName().Name, ours)) continue;
                Type[] ts;
                try { ts = a.GetTypes(); } catch (Exception) { continue; }
                foreach (Type t in ts)
                {
                    if (t.IsAbstract || !t.IsSubclassOf(typeof(VisitCommunityLot)) || sVclSubPatched.Contains(t) || t.GetMethod("Run", inst, null, Type.EmptyTypes, null) == null) continue;
                    sVclSubPatched.Add(t);
                    int slot = sVclSubPatched.Count - 1;
                    if (slot >= kVclSubHooks.Length) { AppliedLog().Add(t.FullName + ".Run: no unseenfbtrip hook slot, left alone"); continue; }
                    Patch(t, "Run", Type.EmptyTypes, kVclSubHooks[slot], "RunHook", "RunOrig");
                }
            }
        }

        static List<object> RecentLog()
        {
            List<object> l = AppDomain.CurrentDomain.GetData("ts3fixes.recent") as List<object>;
            if (l == null) { l = new List<object>(); AppDomain.CurrentDomain.SetData("ts3fixes.recent", l); }
            return l;
        }

    }
}
