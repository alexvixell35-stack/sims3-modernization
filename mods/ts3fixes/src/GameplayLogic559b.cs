using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.MinorPets;
using Sims3.Gameplay.Objects.Misc;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Second pass of the docs/559 gameplay scan: the ranked loopholes of the first pass, each family one switch, off by default.
    //
    // bandjam (tuning kBandJam; docs/559 §6). JamController [verified, decompiled]:
    //  (a) SetSweetMove tests `(j != null && s != Exited) || s != PlayingSweetMove`: a null jammer throws, and any state but
    //      Exited passes, so a Sweet Move clicked on a jammer that is already leaving (PendingExit, set by StopPlaying's
    //      RemoveJammer) turns it back into SweetMoveRequested. RemoveJammer then waits `while (state != Exited)` in a Sleep
    //      loop with no exit test, but the state cycles back to Playing and never reaches Exited: the leaving Sim hangs in
    //      the jam for good. Fix: the own request is applied only to a jammer that is Playing (the state the interaction's
    //      Test requires) or already requested; the other jammers' 50% chance runs as EA's. Counters [2458].
    //  (b) RemoveJammer(BandInstrument) (instrument DoReset) drops the entry without stopping the jam's SoundArray when it
    //      was the last jammer, and RemoveJammer(Sim) returns early for a Sim no longer listed, so the jam music loops on
    //      with nobody playing. Fix: after either overload, an empty controller stops its sound (EA's StopSoundArray) [2459].
    //  Errors [2460].
    //
    // statueline (tuning kStatueLine; docs/559 §7). BoxOfDanger+DoTrick: the Human Statue trick queues every performer in a
    // static, persisted Queue<Sim> and waits `while (line.Peek() != Actor) Sleep(1)` inside an animation event [verified].
    // Cleanup does `if (line.Contains(Actor)) line.Dequeue()`, which removes the head (another performer) instead of Actor
    // when Actor was cancelled or reset while waiting; Actor's stale entry then blocks every later statue trick, and the
    // line is saved with the game. Fix: Cleanup removes Actor itself; before each pose wait, entries of Sims that are gone
    // or not doing a trick are dropped (a saved stale line clears on the first trick). Counters [2461] entries fixed
    // (wrong dequeues avoided + stale entries dropped; wrong dequeues counted with the switch off too), [2462] errors.
    //
    // cleanflag (tuning kCleanFlag; docs/559 §8). FirePit+PutAway and PicnicBasket+CleanUp set BeingCleaned, then return
    // true on their "already cleaned up" exit without clearing it [verified]. A pit then can never be lit, cleaned or
    // enjoyed (CanLight/CanEnjoy/CanClean all need !BeingCleaned) and a basket served again can never be eaten from or
    // cleaned up (two Sims queueing Clean Up is enough), until the object is reset. Fix: when the run that started with
    // the flag clear ends with it still set, it is cleared. Counters [2463] flags cleared (counted with the switch off
    // too), [2464] errors.
    //
    // birdtvnull (tuning kBirdTvNull; docs/559 §9). MinorPetBirdLarge.ShouldReactToTv dereferences TV.CurrentChannel for
    // every TurnedOn TV in range; CurrentChannel is null for a TV switched on while broken (TurnOnOff returns before a
    // channel is set) or with a channel from a removed pack [verified]. The bird's idle behaviour throws every time and
    // the talking bird stops reacting. Fix: the same test with TVs that have no channel skipped. Counters [2465] TVs
    // skipped, [2466] errors.
    public static partial class Fixes
    {
        static FieldInfo sBjSound, sSlLine, sBirdRadius;
        static MethodInfo sBjStop;

        static void ApplyGameplayLogic559b()
        {
            const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // bandjam
            sBjSound = typeof(JamController).GetField("mSoundArray", inst);
            sBjStop = typeof(JamController).GetMethod("StopSoundArray", inst);
            Patch(typeof(JamController), "SetSweetMove", typeof(BjHook), "SweetHook", "SweetOrig");
            if (sBjSound == null || sBjStop == null) AppliedLog().Add("bandjam: JamController sound members not found, sound part not patched");
            else
            {
                Patch(typeof(JamController), "RemoveJammer", new Type[] { typeof(BandInstrument) }, typeof(BjHook), "RemoveInstHook", "RemoveInstOrig");
                Patch(typeof(JamController), "RemoveJammer", new Type[] { typeof(Sim), typeof(bool) }, typeof(BjHook), "RemoveSimHook", "RemoveSimOrig");
            }
            // statueline
            sSlLine = typeof(BoxOfDanger).GetField("mStatueLine", stat);
            if (sSlLine == null) AppliedLog().Add("statueline: BoxOfDanger.mStatueLine not found, not patched");
            else
            {
                Patch(typeof(BoxOfDanger.DoTrick), "Cleanup", typeof(SlHook), "CleanupHook", "CleanupOrig");
                Patch(typeof(BoxOfDanger.DoTrick), "LowLODPose", typeof(SlHook), "LowHook", "LowOrig");
                Patch(typeof(BoxOfDanger.DoTrick), "HighLODPose", typeof(SlHook), "HighHook", "HighOrig");
            }
            // cleanflag
            Type pitPutAway = typeof(FirePit).GetNestedType("PutAway", BindingFlags.Public | BindingFlags.NonPublic);
            if (pitPutAway == null) AppliedLog().Add("cleanflag: FirePit+PutAway not found, fire pit part not patched");
            else Patch(pitPutAway, "Run", typeof(CfPitHook), "RunHook", "RunOrig");
            Patch(typeof(PicnicBasket.CleanUp), "Run", typeof(CfBasketHook), "RunHook", "RunOrig");
            // birdtvnull
            sBirdRadius = typeof(MinorPetBirdLarge).GetField("kRadiusToCheckForTvReactions", stat);
            if (sBirdRadius == null) AppliedLog().Add("birdtvnull: kRadiusToCheckForTvReactions not found, not patched");
            else Patch(typeof(MinorPetBirdLarge), "ShouldReactToTv", typeof(BirdHook), "ReactHook", "ReactOrig");
        }

        // ---------- bandjam ----------
        public class BjHook
        {
            public void SweetHook(Sim actor, int minLevelForAvailablity)
            {
                JamController jc = (object)this as JamController;
                JamInfo own = null;
                JamInfo.PlayState keep = JamInfo.PlayState.Undefined;
                bool refuse = false;
                try
                {
                    own = jc == null ? null : jc.GetJammerInfo(actor);
                    refuse = BjRefuse(own);
                    if (refuse) { PatchCounters()[2458]++; keep = own == null ? keep : own.CurrentPlayState; }
                    if (refuse && own == null && ShortcutOn("bandjam")) return;  // EA throws here: not jamming, nothing to request
                }
                catch (Exception) { PatchCounters()[2460]++; }
                SweetOrig(actor, minLevelForAvailablity);
                // EA's body has no yield: restoring right after it undoes only its own-state line, the others' rolls stay
                if (refuse && own != null && ShortcutOn("bandjam")) own.CurrentPlayState = keep;
            }
            public void SweetOrig(Sim actor, int minLevelForAvailablity) { } // receives JamController.SetSweetMove's original body

            public void RemoveInstHook(BandInstrument bandInstrument)
            {
                RemoveInstOrig(bandInstrument);
                BjStopIfEmpty((object)this as JamController);
            }
            public void RemoveInstOrig(BandInstrument bandInstrument) { } // receives JamController.RemoveJammer(BandInstrument)

            public void RemoveSimHook(Sim actor, bool playExitAnim)
            {
                RemoveSimOrig(actor, playExitAnim);
                BjStopIfEmpty((object)this as JamController);
            }
            public void RemoveSimOrig(Sim actor, bool playExitAnim) { } // receives JamController.RemoveJammer(Sim, bool)
        }

        // EA's own-state line should run only for a jammer that can start a sweet move.
        static bool BjRefuse(JamInfo own)
        {
            if (own == null) return true;
            JamInfo.PlayState s = own.CurrentPlayState;
            return s != JamInfo.PlayState.Playing && s != JamInfo.PlayState.SweetMoveRequested;
        }

        static void BjStopIfEmpty(JamController jc)
        {
            try
            {
                if (jc == null || jc.NumJammers != 0 || sBjSound.GetValue(jc) == null || !ShortcutOn("bandjam")) return;
                sBjStop.Invoke(jc, null);
                PatchCounters()[2459]++;
            }
            catch (Exception) { PatchCounters()[2460]++; }
        }

        // ---------- statueline ----------
        public class SlHook
        {
            public void CleanupHook()
            {
                try { SlBeforeCleanup((object)this as BoxOfDanger.DoTrick); } catch (Exception) { PatchCounters()[2462]++; }
                CleanupOrig();
            }
            public void CleanupOrig() { } // receives BoxOfDanger+DoTrick.Cleanup's original body

            public void LowHook()
            {
                try { SlPurge(); } catch (Exception) { PatchCounters()[2462]++; }
                LowOrig();
            }
            public void LowOrig() { } // receives BoxOfDanger+DoTrick.LowLODPose's original body

            public void HighHook()
            {
                try { SlPurge(); } catch (Exception) { PatchCounters()[2462]++; }
                HighOrig();
            }
            public void HighOrig() { } // receives BoxOfDanger+DoTrick.HighLODPose's original body
        }

        // Removes one Sim from the line wherever it stands (order of the others kept).
        static void SlRemove(Queue<Sim> line, Sim who)
        {
            Sim[] all = line.ToArray();
            line.Clear();
            foreach (Sim s in all) if (!ReferenceEquals(s, who)) line.Enqueue(s);
        }

        // True when EA's Cleanup would dequeue another performer (Actor listed, not at the head).
        static bool SlWrongDequeue(Queue<Sim> line, Sim actor)
        {
            return line != null && line.Count > 0 && line.Contains(actor) && !ReferenceEquals(line.Peek(), actor);
        }

        static void SlBeforeCleanup(BoxOfDanger.DoTrick ia)
        {
            Queue<Sim> line = ia == null ? null : sSlLine.GetValue(null) as Queue<Sim>;
            if (!SlWrongDequeue(line, ia.Actor)) return;
            PatchCounters()[2461]++;
            if (ShortcutOn("statueline")) SlRemove(line, ia.Actor);  // EA's Contains is then false: it dequeues nothing
        }

        // Before a performer joins the wait: entries of Sims gone or no longer doing a trick are dropped.
        static void SlPurge()
        {
            if (!ShortcutOn("statueline")) return;
            Queue<Sim> line = sSlLine.GetValue(null) as Queue<Sim>;
            if (line == null || line.Count == 0) return;
            foreach (Sim s in line.ToArray())
            {
                bool alive = !ReferenceEquals(s, null) && !s.HasBeenDestroyed && s.InteractionQueue != null
                    && s.InteractionQueue.GetCurrentInteraction() is BoxOfDanger.DoTrick;
                if (alive) continue;
                SlRemove(line, s);
                PatchCounters()[2461]++;
            }
        }

        // ---------- cleanflag ----------
        public class CfPitHook
        {
            public bool RunHook()
            {
                InteractionInstance ia = (object)this as InteractionInstance;
                FirePit pit = ia == null ? null : ia.Target as FirePit;
                bool was = pit != null && pit.BeingCleaned;
                try { return RunOrig(); }
                finally
                {
                    try { if (pit != null && CfLeftSet(was, pit.BeingCleaned)) pit.BeingCleaned = false; } catch (Exception) { PatchCounters()[2464]++; }
                }
            }
            public bool RunOrig() { return false; } // receives FirePit+PutAway.Run's original body
        }

        public class CfBasketHook
        {
            public bool RunHook()
            {
                InteractionInstance ia = (object)this as InteractionInstance;
                PicnicBasket basket = ia == null ? null : ia.Target as PicnicBasket;
                bool was = basket != null && basket.BeingCleaned;
                try { return RunOrig(); }
                finally
                {
                    try { if (basket != null && CfLeftSet(was, basket.BeingCleaned)) basket.BeingCleaned = false; } catch (Exception) { PatchCounters()[2464]++; }
                }
            }
            public bool RunOrig() { return false; } // receives PicnicBasket+CleanUp.Run's original body
        }

        // The run set the flag (clear at entry) and left it set: counted; true = clear it (switch on).
        static bool CfLeftSet(bool atEntry, bool atExit)
        {
            if (atEntry || !atExit) return false;
            PatchCounters()[2463]++;
            return ShortcutOn("cleanflag");
        }

        // ---------- birdtvnull ----------
        public class BirdHook
        {
            public bool ReactHook(out MinorPetBirdLarge.SpeechType speechTypeToUse)
            {
                if (!ShortcutOn("birdtvnull")) return ReactOrig(out speechTypeToUse);
                try { return BirdReact((object)this as MinorPetBirdLarge, out speechTypeToUse); }
                catch (Exception) { PatchCounters()[2466]++; speechTypeToUse = MinorPetBirdLarge.SpeechType.Squawk; return false; }
            }
            public bool ReactOrig(out MinorPetBirdLarge.SpeechType speechTypeToUse) { speechTypeToUse = MinorPetBirdLarge.SpeechType.Squawk; return false; } // receives MinorPetBirdLarge.ShouldReactToTv
        }

        // A TV the bird can react to: on and with a channel (EA dereferences the channel unconditionally).
        static bool BirdTvChannel(TV tv, out TVChannelData channel)
        {
            channel = null;
            if (tv == null || !tv.TurnedOn) return false;
            channel = tv.CurrentChannel;
            if (channel != null) return true;
            PatchCounters()[2465]++;
            return false;
        }

        // EA's ShouldReactToTv with TVs that have no channel skipped.
        static bool BirdReact(MinorPetBirdLarge bird, out MinorPetBirdLarge.SpeechType speechTypeToUse)
        {
            speechTypeToUse = MinorPetBirdLarge.SpeechType.Squawk;
            if (bird == null) return false;
            if (bird.CurrentBirdState == Bird.BirdState.Suspending || bird.CurrentBirdState == Bird.BirdState.Suspended) return false;
            bool love = bird.BirdKnowsSpeechType(MinorPetBirdLarge.SpeechType.Love);
            bool scared = bird.BirdKnowsSpeechType(MinorPetBirdLarge.SpeechType.Scared);
            if (!love && !scared) return false;
            bool romantic = false, horror = false;
            TV[] tvs = Sims3.Gameplay.Queries.GetObjects<TV>(bird.Position, (float)sBirdRadius.GetValue(null));
            TVChannelData ch;
            if (love) foreach (TV tv in tvs) if (BirdTvChannel(tv, out ch) && ch.IsRomanticChannel) { romantic = true; break; }
            if (scared) foreach (TV tv in tvs) if (BirdTvChannel(tv, out ch) && ch.IsHorrorChannel) { horror = true; break; }
            if (romantic && horror)
            {
                speechTypeToUse = RandomUtil.CoinFlip() ? MinorPetBirdLarge.SpeechType.Love : MinorPetBirdLarge.SpeechType.Scared;
                return true;
            }
            if (romantic) { speechTypeToUse = MinorPetBirdLarge.SpeechType.Love; return true; }
            if (horror) { speechTypeToUse = MinorPetBirdLarge.SpeechType.Scared; return true; }
            return false;
        }

        // ---------- harness checks (throwaway objects, never added to the world; nothing in the world changes) ----------
        // BandJamCheck run: (a) a throwaway JamController (never attached to an instrument) with one jammer in PendingExit
        //   (leaving) gets EA's SetSweetMove through the hook: off BROKEN = state turned into SweetMoveRequested (the
        //   leaver would hang); on FIXED = still PendingExit. (b) the stop rule for an empty controller with a sound array
        //   placeholder: off BROKEN = kept; on FIXED = stop chosen (decision only, no sound object exists).
        public static object BandJamCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "bandjam: refused requests 2458=" + c[2458] + ", sounds stopped 2459=" + c[2459] + ", errors 2460=" + c[2460];
            if (what == "result") return counters;
            if (what != "run") return "bandjam <run|result>";
            bool on = ShortcutOn("bandjam");
            Sim placeholder = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            JamController jc = new JamController();
            JamInfo info = (JamInfo)FormatterServices.GetUninitializedObject(typeof(JamInfo));
            info.Actor = placeholder;
            info.CurrentPlayState = JamInfo.PlayState.PendingExit;
            FieldInfo list = typeof(JamController).GetField("mJammers", BindingFlags.Instance | BindingFlags.NonPublic);
            List<JamInfo> jammers = new List<JamInfo>();
            jammers.Add(info);
            list.SetValue(jc, jammers);
            jc.SetSweetMove(placeholder, 99);  // 99: no other jammer qualifies, no skill lookup on the placeholder
            string a = info.CurrentPlayState == JamInfo.PlayState.PendingExit ? "FIXED" : info.CurrentPlayState == JamInfo.PlayState.SweetMoveRequested ? "BROKEN" : "UNEXPECTED";
            jammers.Clear();
            bool stop = on && jc.NumJammers == 0;
            string b = stop ? "FIXED" : "BROKEN";
            string verdict = a == b ? a : "UNEXPECTED";
            return "bandjam check (switch " + (on ? "on" : "off") + "): sweet move on a leaving jammer " + a + " (state " + info.CurrentPlayState + "), empty-jam sound stop " + b + "; verdict: " + verdict + "; " + counters;
        }

        // StatueLineCheck run: a local copy of the line [other, actor] (placeholder Sims, the real static line untouched):
        //   off BROKEN = EA's Cleanup step removes `other`; on FIXED = `actor` removed, `other` kept at the head.
        public static object StatueLineCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "statueline: entries fixed 2461=" + c[2461] + ", errors 2462=" + c[2462];
            if (what == "result") return counters;
            if (what != "run") return "statueline <run|result>";
            bool on = ShortcutOn("statueline");
            Sim other = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            Sim actor = (Sim)FormatterServices.GetUninitializedObject(typeof(Sim));
            Queue<Sim> line = new Queue<Sim>();
            line.Enqueue(other);
            line.Enqueue(actor);
            bool wrong = SlWrongDequeue(line, actor);
            if (wrong && on) SlRemove(line, actor);
            if (line.Contains(actor)) line.Dequeue();  // EA's Cleanup step
            bool otherKept = line.Contains(other), actorGone = !line.Contains(actor);
            string verdict = !wrong ? "UNEXPECTED" : otherKept && actorGone ? "FIXED" : !otherKept && !actorGone ? "BROKEN" : "UNEXPECTED";
            return "statueline check (switch " + (on ? "on" : "off") + "): head kept=" + otherKept + ", cancelled performer removed=" + actorGone + "; verdict: " + verdict + "; " + counters;
        }

        // CleanFlagCheck run: a throwaway PicnicBasket (uninitialized, never placed): the end-of-run rule for a run that
        //   set BeingCleaned and left it set. off BROKEN = flag kept (basket unusable); on FIXED = flag cleared; a flag
        //   already set at entry (another cleaner) is never touched.
        public static object CleanFlagCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "cleanflag: flags cleared/seen 2463=" + c[2463] + ", errors 2464=" + c[2464];
            if (what == "result") return counters;
            if (what != "run") return "cleanflag <run|result>";
            bool on = ShortcutOn("cleanflag");
            PicnicBasket basket = (PicnicBasket)FormatterServices.GetUninitializedObject(typeof(PicnicBasket));
            basket.BeingCleaned = true;
            if (CfLeftSet(false, basket.BeingCleaned)) basket.BeingCleaned = false;
            bool otherKept = !CfLeftSet(true, true);
            string verdict = !otherKept ? "UNEXPECTED" : basket.BeingCleaned ? "BROKEN" : "FIXED";
            return "cleanflag check (switch " + (on ? "on" : "off") + "): flag after an already-cleaned exit=" + basket.BeingCleaned + ", other cleaner's flag kept=" + otherKept + "; verdict: " + verdict + "; " + counters;
        }

        // BirdTvNullCheck run: a throwaway TV (uninitialized, never placed) switched on with no channel. off BROKEN = EA's
        //   expression (tv.CurrentChannel.IsRomanticChannel) throws; on FIXED = the fixed test skips the TV.
        public static object BirdTvNullCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "birdtvnull: TVs skipped 2465=" + c[2465] + ", errors 2466=" + c[2466];
            if (what == "result") return counters;
            if (what != "run") return "birdtvnull <run|result>";
            bool on = ShortcutOn("birdtvnull");
            TV tv = (TV)FormatterServices.GetUninitializedObject(typeof(TV));
            tv.TurnedOn = true;
            string r;
            if (on)
            {
                TVChannelData ch;
                r = BirdTvChannel(tv, out ch) ? "UNEXPECTED" : "FIXED";
            }
            else
            {
                try { bool x = tv.CurrentChannel.IsRomanticChannel; r = x ? "UNEXPECTED" : "UNEXPECTED"; }
                catch (NullReferenceException) { r = "BROKEN"; }
            }
            return "birdtvnull check (switch " + (on ? "on" : "off") + "): TV on with no channel; verdict: " + r + "; " + counters;
        }
    }
}
