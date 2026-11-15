using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Pets;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // Batch 8 (docs/582 B8; REA tranche 4, docs/584 §7, plus the coverage sweep, docs/586). All default off. simler90
    // changes none of the methods below (docs/data/mod-method-diff.csv; its Secrets.cs has the same key bug), and none
    // was hooked before (docs/data/swap-stack-audit.csv). The other tranche-4 leads are already covered: R2 and R5(b) by
    // fieldtripadult (FieldTripAdult.cs), R1 by prankditchempty (Batch582Logic.cs); R3 and R5(a) are not reachable with
    // the shipped game data (docs/582 B8 notes).
    public static partial class Fixes
    {
        // ---- B8 §1 trashpileimport (tuning kTrashPileImport) -------------------------------------------------------------
        // [verified, decompiled] TrashPile.CreateAlarmAndStartAgeEffects runs from OnCreation and again from ImportContent.
        // On import the second run adds a new age alarm without removing the first (which later re-fires Fresh->Old: age
        // back to Old, a second smell broadcaster and stink effect over the live ones). A pile saved as Ancient goes straight
        // to AlarmCallbackOldToAncient, which dereferences mDisgustedEffectBroadcast, made only by Fresh->Old: the import
        // throws. Fix, in the age setup: the earlier alarm, broadcaster and stink effect are released first, and an Ancient
        // pile is built through EA's own Old step, then EA's Old->Ancient step. Household-visible: an imported pile on a
        // household lot ages and smells once, as EA meant.
        // Counters: [1233] stale age setups released, [1234] Ancient piles built through Old, [1235] seen with the switch
        // off, [1236] errors.
        static readonly FieldInfo sTpAlarm = typeof(TrashPile).GetField("mAgeAlarm", kInstAll);
        static readonly FieldInfo sTpBroad = typeof(TrashPile).GetField("mDisgustedEffectBroadcast", kInstAll);
        static readonly MethodInfo sTpToAncient = typeof(TrashPile).GetMethod("AlarmCallbackOldToAncient", kInstAll, null, Type.EmptyTypes, null);

        // Pure: bit 1 = release the earlier setup, bit 2 = build an Ancient pile through Old. 0 = EA's code unchanged.
        internal static int TpPlan(bool alarmSet, bool hasEffects, bool ancient)
        {
            int r = alarmSet || hasEffects ? 1 : 0;
            if (ancient) r |= 2;
            return r;
        }

        static void ApplyTrashPileImport()
        {
            if (sTpAlarm == null || sTpBroad == null || sTpToAncient == null)
            {
                AppliedLog().Add("trashpileimport: not found: TrashPile.mAgeAlarm / mDisgustedEffectBroadcast / AlarmCallbackOldToAncient");
                return;
            }
            Patch(typeof(TrashPile), "CreateAlarmAndStartAgeEffects", Type.EmptyTypes, typeof(BTpHook), "AgeHook", "AgeOrig");
        }

        // A delegate, not MethodInfo.Invoke, which refuses swapped methods in the game's Mono (ComboChecks.cs).
        delegate void TpStepFn();

        // Derives from GameObject so RemoveAlarm compiles; at run time `this` is the TrashPile.
        public abstract class BTpHook : GameObject
        {
            public void AgeHook()
            {
                int plan = 0;
                TrashPile tp = null;
                try
                {
                    object o = this;
                    tp = (TrashPile)o;
                    AlarmHandle alarm = (AlarmHandle)sTpAlarm.GetValue(o);
                    ReactionBroadcaster rb = sTpBroad.GetValue(o) as ReactionBroadcaster;
                    bool alarmSet = alarm != AlarmHandle.kInvalidHandle;
                    plan = TpPlan(alarmSet, rb != null || tp.StinkEffect != null, tp.TrashPileAge == TrashPile.PileAge.Ancient);
                    if (plan != 0 && !ShortcutOn("trashpileimport"))
                    {
                        // only a stale setup or an Ancient pile without a broadcaster misbehaves in EA
                        if ((plan & 1) != 0 || rb == null) PatchCounters()[1235]++;
                        plan = 0;
                    }
                    if ((plan & 1) != 0)
                    {
                        PatchCounters()[1233]++;
                        if (alarmSet) { RemoveAlarm(alarm); sTpAlarm.SetValue(o, AlarmHandle.kInvalidHandle); }
                        if (rb != null) { rb.Dispose(); sTpBroad.SetValue(o, null); }
                        if (tp.StinkEffect != null)
                        {
                            tp.StinkEffect.Stop(VisualEffect.TransitionType.SoftTransition);
                            tp.StinkEffect.Dispose();
                            tp.StinkEffect = null;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1236]++; plan = 0; }
                if ((plan & 2) == 0) { AgeOrig(); return; }
                PatchCounters()[1234]++;
                tp.TrashPileAge = TrashPile.PileAge.Old;
                AgeOrig();                          // EA: age alarm, then Fresh->Old (broadcaster, stink, Elder alarm)
                ((TpStepFn)Delegate.CreateDelegate(typeof(TpStepFn), tp, sTpToAncient))();  // EA: Old->Ancient
            }
            public void AgeOrig() { } // receives TrashPile.CreateAlarmAndStartAgeEffects
        }

        // ---- B8 §2 catgymwatch (tuning kCatGymWatch) ---------------------------------------------------------------------
        // [verified, decompiled] CatJungleGym.OnEnterWatchPetBroadcaster pushes WatchCatJungleGym on a passer-by without
        // setting JungleGymRef (the player's Watch interaction sets it); WatchCatJungleGym.Run returns false at once when it
        // is null, so the broadcast watch/laugh reaction never plays. Fix at Run: a missing JungleGymRef is filled with the
        // gym the watched cat is using (the object InLoop already checks: Target.CurrentInteraction.Target). With no such
        // gym EA's code runs (returns false). Household-visible: household Sims passing a cat on the gym may now stop and
        // watch, as EA meant.
        // Counters: [1237] gym references filled, [1238] seen with the switch off, [1239] errors.
        // Pure: 0 = EA's code, 1 = fill the reference, 2 = would fill but the switch is off.
        internal static int CgDecide(bool on, bool refSet, bool catOnGym)
        {
            if (refSet || !catOnGym) return 0;
            return on ? 1 : 2;
        }

        static void ApplyCatGymWatch()
        {
            Patch(typeof(CatJungleGym.WatchCatJungleGym), "Run", Type.EmptyTypes, typeof(BCgHook), "RunHook", "RunOrig");
        }

        public class BCgHook
        {
            public bool RunHook()
            {
                try
                {
                    object o = this;
                    CatJungleGym.WatchCatJungleGym w = (CatJungleGym.WatchCatJungleGym)o;
                    if (w.JungleGymRef == null)
                    {
                        Sim cat = w.Target;
                        CatJungleGym gym = cat != null && cat.CurrentInteraction != null ? cat.CurrentInteraction.Target as CatJungleGym : null;
                        int k = CgDecide(ShortcutOn("catgymwatch"), false, gym != null);
                        if (k == 1) { PatchCounters()[1237]++; w.JungleGymRef = gym; }
                        else if (k == 2) PatchCounters()[1238]++;
                    }
                }
                catch (Exception) { PatchCounters()[1239]++; }
                return RunOrig();
            }
            public bool RunOrig() { return false; } // receives CatJungleGym.WatchCatJungleGym.Run
        }

        // ---- B8 §3 badsecretkey (tuning kBadSecretKey) -------------------------------------------------------------------
        // [verified, decompiled] Secrets.GetBestBadSecretTextFor, relationship case: the text key is built as
        // "Gameplay/Socializing/Secrets" + ltr, without the ':' that its own guard (HasLocalizationString) and the good-secret
        // path use, so "Share Bad Secret" with an enemy shows the unlocalized key text instead of the relationship secret.
        // Fix after EA's method: when its result is exactly the text of the wrong key for this pair's relationship, it is
        // replaced by the text of the right key. EA's choice and its sRelationshipSecrets bookkeeping are unchanged.
        // Player-directed only (OnShareBadSecret skips autonomous). Household-visible: yes (the notification text).
        // Counters: [1240] texts corrected, [1241] seen with the switch off, [1242] errors.
        // Pure: is EA's result the wrong key's text?
        internal static bool SxIsWrong(string result, string wrongText)
        {
            return result != null && wrongText != null && wrongText.Length > 2 && result == wrongText;
        }

        static void ApplyBadSecretKey()
        {
            Patch(typeof(Secrets), "GetBestBadSecretTextFor", new Type[] { typeof(Sim), typeof(Sim) }, typeof(BSxHook), "TextHook", "TextOrig");
        }

        public class BSxHook
        {
            public static string TextHook(Sim speaker, Sim listener)
            {
                string r = TextOrig(speaker, listener);
                try
                {
                    if (r == null || speaker == null || listener == null) return r;
                    Relationship rel = Relationship.Get(speaker, listener, false);
                    if (rel == null || rel.LTR == null || rel.LTR.IsPositive) return r;
                    LongTermRelationshipTypes ltr = rel.LTR.CurrentLTR;
                    if (ltr == LongTermRelationshipTypes.Default) return r;
                    bool[] g = new bool[] { speaker.SimDescription.IsFemale, listener.SimDescription.IsFemale };
                    string wrong = "\"" + Localization.LocalizeString(g, "Gameplay/Socializing/Secrets" + ltr) + "\"";
                    if (!SxIsWrong(r, wrong)) return r;
                    if (!ShortcutOn("badsecretkey")) { PatchCounters()[1241]++; return r; }
                    string right = Localization.LocalizeString(g, "Gameplay/Socializing/Secrets:" + ltr);
                    if (string.IsNullOrEmpty(right)) return r;
                    PatchCounters()[1240]++;
                    return "\"" + right + "\"";
                }
                catch (Exception) { PatchCounters()[1242]++; return r; }
            }
            public static string TextOrig(Sim speaker, Sim listener) { return null; } // receives Secrets.GetBestBadSecretTextFor
        }

        // ---- B8 §4 traitlistenerremove (tuning kTraitListenerRemove) -----------------------------------------------------
        // [verified, decompiled] TraitManager.RemoveElement(ulong) reads TraitListener from GetTraitFromDictionary(guid),
        // a fresh Clone() whose listener is always null; the Sim's own trait (in mValues, where AddElement stored the clone
        // that holds the listener) keeps its EventTracker listeners. A Sim who loses Evil, Couch Potato, Anti-TV, Loser,
        // Computer Whiz or Supernatural Fan still reacts as before for the rest of the session. Fix before EA's method: the
        // Sim's own trait's listener is removed (Trait.Fixup does the same on re-instantiation). Household-visible: yes.
        // Counters: [1243] listeners removed, [1244] left with the switch off, [1245] errors.
        // Pure: remove the live trait's listener?
        internal static bool TlRemove(bool on, bool liveHasListener) { return on && liveHasListener; }

        static void ApplyTraitListenerRemove()
        {
            Patch(typeof(TraitManager), "RemoveElement", new Type[] { typeof(ulong) }, typeof(BTlHook), "RemoveHook", "RemoveOrig");
        }

        public class BTlHook
        {
            public void RemoveHook(ulong guid)
            {
                try
                {
                    object o = this;
                    Trait live = ((GenericManager<TraitNames, Trait, Trait>)o).GetElement(guid);
                    if (live != null && live.TraitListener != null)
                    {
                        if (TlRemove(ShortcutOn("traitlistenerremove"), true))
                        {
                            PatchCounters()[1243]++;
                            live.TraitListener.Remove();
                            live.TraitListener = null;
                        }
                        else PatchCounters()[1244]++;
                    }
                }
                catch (Exception) { PatchCounters()[1245]++; }
                if (TnRemove(this, guid)) return;  // traitremovenull (Batch582iLogic.cs, docs/582 B9 §1): EA would throw
                RemoveOrig(guid);
            }
            public void RemoveOrig(ulong guid) { } // receives TraitManager.RemoveElement(ulong)
        }

        // ---- B8 §6 portasknohome (tuning kPortAskNoHome) -----------------------------------------------------------------
        // [verified, decompiled + runtime x2 (coverage docs/586 §8)] SocialTest.AskToChangePortTest (Island Paradise
        // houseboats) calls lotHome.IsHouseboatLot() on the actor's and target's home lots without a null test: for a Sim
        // with no home lot (townie, tourist, homeless household) it throws NullReferenceException out of the social
        // availability pass (menu build and autonomy) for that target. A Sim without a home cannot ask to change port, so
        // the fix returns false there; otherwise EA's test runs. Household-visible: a homeless selected Sim's social menu
        // builds where it errored; the test's answer is otherwise unchanged.
        // Counters: [1249] throws avoided, [1250] seen with the switch off, [1251] errors.
        // Pure: would EA's test throw? It does when it reaches IsHouseboatLot on a null lot.
        internal static bool PtThrows(bool targetAtHome, bool sameHome, bool actorHomeNull, bool targetHomeNull)
        {
            if (!targetAtHome || sameHome) return false;   // EA returns false before any IsHouseboatLot call
            return actorHomeNull || targetHomeNull;
        }

        static void ApplyPortAskNoHome()
        {
            Patch(typeof(SocialTest), "AskToChangePortTest", typeof(BPtHook), "TestHook", "TestOrig");
        }

        public class BPtHook
        {
            public static bool TestHook(Sim actor, Sim target, ActiveTopic topic, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback)
            {
                try
                {
                    if (actor != null && target != null)
                    {
                        Lot a = actor.LotHome, t = target.LotHome;
                        if (PtThrows(target.LotCurrent == t, a == t, a == null, t == null))
                        {
                            if (ShortcutOn("portasknohome")) { PatchCounters()[1249]++; return false; }
                            PatchCounters()[1250]++;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1251]++; }
                return TestOrig(actor, target, topic, isAutonomous, ref greyedOutTooltipCallback);
            }
            public static bool TestOrig(Sim actor, Sim target, ActiveTopic topic, bool isAutonomous, ref GreyedOutTooltipCallback greyedOutTooltipCallback) { return false; } // receives SocialTest.AskToChangePortTest
        }

        // ---- B8 §7 lecturebroadcaster (tuning kLectureBroadcaster) -------------------------------------------------------
        // [verified, decompiled + tuning] Education.BeIntrigueing (Give Great Lecture) runs `new ReactionBroadcaster(actor,
        // kGreatLectureBroadcastParams, BeIntriguedCallback)` and never stores it; the shipped tuning (GameplayData.package)
        // sets TimeDuration -1, so each lecture leaves an endless broadcaster on the lecturer (radius 3, pulse every 20 sim
        // minutes). After N lectures nearby Sims get N friendly STC/LTR pulses per 20 minutes and the lot's reaction work
        // grows. Political.BeInspirational has the same shape but a tuned duration (30). Fix: one broadcaster per lecturer;
        // a new lecture disposes the previous one, then builds EA's broadcaster with EA's params and callback. The
        // remaining one keeps EA's tuned (endless) duration: choosing a duration is a design call (docs/582 B8 §7).
        // Household-visible: a household lecturer's audience gets one pulse per 20 minutes instead of one per lecture given.
        // Counters: [1252] earlier broadcasters disposed, [1253] repeat lectures with the switch off, [1254] errors.
        static readonly FieldInfo sLbParams = typeof(Education).GetField("kGreatLectureBroadcastParams", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo sLbCallback = typeof(Education).GetMethod("BeIntriguedCallback", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly Dictionary<Sim, ReactionBroadcaster> sLbBySim = new Dictionary<Sim, ReactionBroadcaster>();
        static readonly Dictionary<Sim, bool> sLbSeen = new Dictionary<Sim, bool>();

        // Pure: dispose an earlier broadcaster first?
        internal static bool LbReplace(bool on, bool hasEarlier) { return on && hasEarlier; }

        static void LbPrune()
        {
            List<Sim> gone = null;
            foreach (Sim s in sLbSeen.Keys) if (s == null || s.HasBeenDestroyed) (gone ?? (gone = new List<Sim>())).Add(s);
            if (gone == null) return;
            foreach (Sim s in gone) { sLbSeen.Remove(s); sLbBySim.Remove(s); }  // the lot drops a destroyed Sim's broadcasters
        }

        static void ApplyLectureBroadcaster()
        {
            if (sLbParams == null || sLbCallback == null) { AppliedLog().Add("lecturebroadcaster: not found: Education.kGreatLectureBroadcastParams / BeIntriguedCallback"); return; }
            Patch(typeof(Education), "BeIntrigueing", new Type[] { typeof(Sim) }, typeof(BLbHook), "LectureHook", "LectureOrig");
        }

        public class BLbHook
        {
            public static void LectureHook(Sim actor)
            {
                bool done = false;
                try
                {
                    if (actor != null)
                    {
                        LbPrune();
                        bool repeat = sLbSeen.ContainsKey(actor);
                        sLbSeen[actor] = true;
                        if (!ShortcutOn("lecturebroadcaster")) { if (repeat) PatchCounters()[1253]++; }
                        else
                        {
                            ReactionBroadcaster earlier;
                            if (LbReplace(true, sLbBySim.TryGetValue(actor, out earlier) && earlier != null))
                            {
                                earlier.Dispose();
                                PatchCounters()[1252]++;
                            }
                            ReactionBroadcaster.BroadcastCallback cb = (ReactionBroadcaster.BroadcastCallback)Delegate.CreateDelegate(typeof(ReactionBroadcaster.BroadcastCallback), sLbCallback);
                            sLbBySim[actor] = new ReactionBroadcaster(actor, (ReactionBroadcasterParams)sLbParams.GetValue(null), cb);
                            done = true;
                        }
                    }
                }
                catch (Exception) { PatchCounters()[1254]++; }
                if (!done) LectureOrig(actor);
            }
            public static void LectureOrig(Sim actor) { } // receives Education.BeIntrigueing
        }

        // ---- B8 §5 weddinglistener (tuning kWeddingListener) -------------------------------------------------------------
        // [verified, decompiled] WeddingParty's constructor adds a world-wide kMarriage listener (OnMarriage) to
        // mEventListeners, PostLoad re-adds it, and CleanUp never removes it; OnMarriage returns Remove only when someone in
        // the party marries. A party that ends without a wedding leaves the listener holding the dead situation, and a later
        // marriage of a former guest runs RemoveGuest / OtherHosts.Add on it. Fix after EA's CleanUp: the party's listeners
        // are removed from EventTracker. Household-visible: player-hosted wedding parties (no visible change; stale state).
        // Counters: [1246] listeners removed, [1247] left with the switch off, [1248] errors.
        static readonly FieldInfo sWlList = typeof(WeddingParty).GetField("mEventListeners", kInstAll);

        // Pure: how many listeners to remove (the ones still registered), 0 with the switch off.
        internal static int WlRemove(bool on, int stillRegistered) { return on ? stillRegistered : 0; }

        static void ApplyWeddingListener()
        {
            if (sWlList == null) { AppliedLog().Add("weddinglistener: not found: WeddingParty.mEventListeners"); return; }
            Patch(typeof(WeddingParty), "CleanUp", Type.EmptyTypes, typeof(BWlHook), "CleanHook", "CleanOrig");
        }

        public class BWlHook : HouseParty  // derives from EA's base so `base.CleanUp()` is EA's non-virtual base call
        {
            public void CleanHook()
            {
                try
                {
                    // partycleanupnull (Batch582iLogic.cs, docs/582 B9 §4): where EA's method would throw, its own steps run
                    // null-safe, then EA's base (HouseParty.CleanUp)
                    if (PcWedding((WeddingParty)(object)this)) base.CleanUp();
                    else CleanOrig();
                }
                finally { After(this); }  // EA's CleanUp can throw on a null host/guest (review 2026-11-15); listeners still go
            }
            static void After(object party)
            {
                try
                {
                    List<EventListener> list = sWlList.GetValue(party) as List<EventListener>;
                    if (list == null) return;
                    int live = 0;
                    foreach (EventListener l in list) if (l != null && EventTracker.ContainsListener(l)) live++;
                    if (live == 0) return;
                    if (WlRemove(ShortcutOn("weddinglistener"), live) == 0) { PatchCounters()[1247] += live; return; }
                    foreach (EventListener l in list) if (l != null) EventTracker.RemoveListener(l);
                    PatchCounters()[1246] += live;
                }
                catch (Exception) { PatchCounters()[1248]++; }
            }
            public void CleanOrig() { } // receives WeddingParty.CleanUp
        }
    }
}
