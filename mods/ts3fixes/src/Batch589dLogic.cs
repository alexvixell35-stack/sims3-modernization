using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Environment;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Plumbing;
using SkillNames = Sims3.Gameplay.Skills.SkillNames;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Batch 11d (docs/589 usage sweep: the U10, U18, U20 and U23 parts not in 11a-c). All default off; all reach the
    // household (as EA intended). Slots 3049-3063 (fixes-1700). U18 and two U20 parts sit in generic or simler90-shared
    // bodies too large to copy and are ts3native IL patches (mods/ilpatch/specs/b11-*.json, [IlPatch] ilp_socialnoeffect,
    // ilp_jambasecleanup, ilp_watchnoplayer); socialnoeffect here only counts what ilp_socialnoeffect fixes.
    public static partial class Fixes
    {
        // ---- B11 §22 bathsituationfx (U10 rest) -------------------------------------------------------------------------
        // [verified, decompiled] Bathtub.TakeBath.Run's route-fail path exits its privacy situation and returns; Cleanup
        // exits it again (mSituation is not cleared): OnExit, CleanUp and Dispose run twice. DoTakeBathPreLoop creates a
        // new bubble-bath effect each bubble bath over the old one, which is only disposed with the tub. Fix: Cleanup
        // skips a situation that already exited (EA's SituationExited); the pre-loop stops and disposes an old effect
        // before EA makes the new one. Counters: [3049] second exits skipped, [3050] old effects disposed, [3051] either
        // seen with the switch off.
        static readonly FieldInfo sBsSit = typeof(Bathtub.TakeBath).GetField("mSituation", kB11All);
        static readonly FieldInfo sBsFx = typeof(Bathtub).GetField("mBubbleBathFX", kB11All);
        static string sBsMissing = "";

        static void ApplyBathSituationFx()
        {
            sBsMissing = SpNames("TakeBath.mSituation", sBsSit, "Bathtub.mBubbleBathFX", sBsFx);
            if (sBsMissing.Length > 0) { AppliedLog().Add("bathsituationfx: not found: " + sBsMissing); return; }
            ApplyTakeBathCleanup();  // the one TakeBath.Cleanup swap (Batch589bLogic.cs BBcHook) calls BsfBeforeCleanup
            Patch(typeof(Bathtub), "DoTakeBathPreLoop", new Type[] { typeof(Sim), typeof(StateMachineClient), typeof(int), typeof(bool), typeof(bool) }, typeof(BBsfTubHook), "PreHook", "PreOrig");
        }

        // Before EA's TakeBath.Cleanup: a privacy situation that already exited is not exited again.
        static void BsfBeforeCleanup(object takeBath)
        {
            if (sBsSit == null) return;
            try
            {
                Sims3.Gameplay.Autonomy.Situation s = sBsSit.GetValue(takeBath) as Sims3.Gameplay.Autonomy.Situation;
                if (s == null || !s.SituationExited) return;
                if (ShortcutOn("bathsituationfx")) { sBsSit.SetValue(takeBath, null); PatchCounters()[3049]++; }
                else PatchCounters()[3051]++;
            }
            catch (Exception) { }
        }

        public class BBsfTubHook
        {
            public void PreHook(Sim s, StateMachineClient smc, int slotIndex, bool haveBubbleBath, bool haveScubaAdventure)
            {
                if (haveBubbleBath)
                {
                    try
                    {
                        VisualEffect old = sBsFx.GetValue(this) as VisualEffect;
                        if (old != null)
                        {
                            if (ShortcutOn("bathsituationfx")) { sBsFx.SetValue(this, null); old.Stop(); old.Dispose(); PatchCounters()[3050]++; }
                            else PatchCounters()[3051]++;
                        }
                    }
                    catch (Exception) { }
                }
                PreOrig(s, smc, slotIndex, haveBubbleBath, haveScubaAdventure);
            }

            public void PreOrig(Sim s, StateMachineClient smc, int slotIndex, bool haveBubbleBath, bool haveScubaAdventure) { }
        }

        // ---- B11 §23 socialnoeffect (U18, counts for ts3native ilp_socialnoeffect) -------------------------------------
        // [verified, decompiled] SocialInteractionA.UpdateConversationWhenSocialStarts sets mTargetEffect from
        // Conversation.UpdateOnSelectingInteraction, which is null when no social rule matches (modded socials); Run then
        // dereferences mTargetEffect.RHS (GetStateMachine, SetStance) and throws. The fix is the IL patch (Run returns
        // false at EA's own early return right after the call); this hook only counts. Counters: [3052] null rules seen,
        // [3053] of those with ilp_socialnoeffect expected on (switch on), [3054] errors.
        static readonly FieldInfo sSnEffect = typeof(SocialInteractionA).GetField("mTargetEffect", kB11All);
        static string sSnMissing = "";

        static void ApplySocialNoEffect()
        {
            sSnMissing = SpNames("SocialInteractionA.mTargetEffect", sSnEffect);
            if (sSnMissing.Length > 0) { AppliedLog().Add("socialnoeffect: not found: " + sSnMissing); return; }
            Patch(typeof(SocialInteractionA), "UpdateConversationWhenSocialStarts", new Type[] { typeof(Sim), typeof(Sim) }, typeof(BSnHook), "StartHook", "StartOrig");
        }

        public class BSnHook
        {
            public void StartHook(Sim initiatingSim, Sim targetSim)
            {
                StartOrig(initiatingSim, targetSim);
                try
                {
                    if (sSnEffect.GetValue(this) != null) return;
                    PatchCounters()[3052]++;
                    if (ShortcutOn("socialnoeffect")) PatchCounters()[3053]++;
                }
                catch (Exception) { PatchCounters()[3054]++; }
            }

            public void StartOrig(Sim initiatingSim, Sim targetSim) { }
        }

        // ---- B11 §24 guitargnomeboost (U20 rest) ------------------------------------------------------------------------
        // [verified, decompiled] Guitar.Play.Run adds the Elvis gnome's guitar skill boost once per gnome in range and
        // takes it off once, so with two or more gnomes the extra boosts stay on the Sim for good (and all of them when
        // the play throws). Fix: what was added is taken off: the extra (n-1) boosts after EA's Run, all n if it threw.
        // Counters: [3055] extra boosts removed, [3056] boosts left on (switch off), [3057] errors.
        static Type sGgPlay;
        static string sGgMissing = "";

        static void ApplyGuitarGnomeBoost()
        {
            sGgPlay = typeof(Guitar).GetNestedType("Play", BindingFlags.Public | BindingFlags.NonPublic);
            sGgMissing = SpNames("Guitar+Play", sGgPlay);
            if (sGgMissing.Length > 0) { AppliedLog().Add("guitargnomeboost: not found: " + sGgMissing); return; }
            Patch(sGgPlay, "Run", Type.EmptyTypes, typeof(BGgHook), "RunHook", "RunOrig");
        }

        // Pure: how many boosts are left on the Sim after the play: EA adds n and removes 1 (none if it threw).
        internal static int GgLeft(int n, bool threw) { return threw ? n : (n > 1 ? n - 1 : 0); }

        public class BGgHook
        {
            public bool RunHook()
            {
                Sim a = null;
                int n = 0;
                try
                {
                    a = ((Interaction<Sim, Guitar>)(object)this).Actor;
                    if (a != null && a.LotCurrent != null)
                        foreach (MagicGnomeElvis g in a.LotCurrent.GetObjects<MagicGnomeElvis>()) if (g.PlayingGuitarInRange(a)) n++;
                }
                catch (Exception) { n = 0; PatchCounters()[3057]++; }
                bool threw = true;
                try
                {
                    bool r = RunOrig();
                    threw = false;
                    return r;
                }
                finally
                {
                    int left = GgLeft(n, threw);
                    if (left > 0)
                    {
                        try
                        {
                            if (ShortcutOn("guitargnomeboost")) { a.SkillManager.AddToSkillGainModifier(SkillNames.Guitar, -left * MagicGnomeElvis.SkillBoost); PatchCounters()[3055]++; }
                            else PatchCounters()[3056]++;
                        }
                        catch (Exception) { PatchCounters()[3057]++; }
                    }
                }
            }

            public bool RunOrig() { return false; }
        }

        // ---- B11 §25 distressgate (U23 rest, part 1) --------------------------------------------------------------------
        // [verified, decompiled] IdleManager.ScheduleCurrentDistressIdles(priority, testOnly) gates its buff branch on
        // `distressIdlePriority == desiredPriority && !testOnly`, so in the test (ExistsHigherPriorityDistressState) a
        // buff's higher-priority distress is never found; only the mood one counts, and lower-priority distress idles are
        // scheduled over a higher buff distress. The mood branch sets the result in the test. Fix: in the test a buff
        // with that priority (that EA would schedule for a child or older, or a toddler) answers true too. Counters:
        // [3058] tests answered by a buff, [3059] missed (switch off), [3060] errors.
        static MethodInfo sDgShould;
        static readonly FieldInfo sDgSim = typeof(IdleManager).GetField("mSim", kB11All);
        static string sDgMissing = "";
        delegate bool DgShouldFn(IdleAnimationPriority priority, BuffInstance bi);

        static void ApplyDistressGate()
        {
            sDgShould = typeof(IdleManager).GetMethod("ShouldScheduleIdles", kB11All, null, new Type[] { typeof(IdleAnimationPriority), typeof(BuffInstance) }, null);
            sDgMissing = SpNames("IdleManager.ShouldScheduleIdles", sDgShould, "IdleManager.mSim", sDgSim);
            if (sDgMissing.Length > 0) { AppliedLog().Add("distressgate: not found: " + sDgMissing); return; }
            Patch(typeof(IdleManager), "ScheduleCurrentDistressIdles", new Type[] { typeof(IdleAnimationPriority), typeof(bool) }, typeof(BDgHook), "ScheduleHook", "ScheduleOrig");
        }

        public class BDgHook
        {
            public bool ScheduleHook(IdleAnimationPriority desiredPriority, bool testOnly)
            {
                bool r = ScheduleOrig(desiredPriority, testOnly);
                if (r || !testOnly || desiredPriority < IdleAnimationPriority.LowestDistressPriority || desiredPriority > IdleAnimationPriority.DistressHigh) return r;
                try
                {
                    Sim sim = sDgSim.GetValue(this) as Sim;
                    if (sim == null || !(sim.SimDescription.ChildOrAbove || sim.SimDescription.Toddler)) return r;
                    DgShouldFn should = null;
                    foreach (BuffInstance b in sim.BuffManager.List)
                    {
                        if (b.GetDistressIdlePriority() != desiredPriority) continue;
                        if (should == null) should = (DgShouldFn)Fixes.Bind(typeof(DgShouldFn), this, sDgShould);
                        if (!should(desiredPriority, b)) continue;
                        if (!ShortcutOn("distressgate")) { PatchCounters()[3059]++; return r; }
                        PatchCounters()[3058]++;
                        return true;
                    }
                }
                catch (Exception) { PatchCounters()[3060]++; }
                return r;
            }

            public bool ScheduleOrig(IdleAnimationPriority desiredPriority, bool testOnly) { return false; }
        }

        // ---- B11 §26 moodidleflavor (U23 rest, part 2) ------------------------------------------------------------------
        // [verified, decompiled; whether the shipped Idles XML has continuation rows is unchecked] IdleManager
        // .ParseMoodIdleData appends a MoodIdle row with an empty MoodFlavor to the previous flavour's idles, but never
        // remembers the flavour (it stays Fulfilled), so every such row is dropped. ParseReactionData, the same pattern,
        // does remember it (`reactionTypes = value2`). Fix: our parse remembers the flavour; if the table was parsed
        // before our hook (startup), the world-load pass reads the Idles XML once and appends the dropped rows. No-op
        // when the XML has no continuation rows. Counters: [3061] rows appended, [3062] rows dropped (switch off),
        // [3063] errors.
        delegate IdleInfo MiRowFn(XmlDbRow row, bool verify);
        static MiRowFn sMiRow;
        static bool sMiOurs;
        static string sMiMissing = "";

        static void ApplyMoodIdleFlavor()
        {
            MethodInfo pr = typeof(IdleManager).GetMethod("ParseIdleInfoFromRow", kB11All, null, new Type[] { typeof(XmlDbRow), typeof(bool) }, null);
            sMiMissing = SpNames("IdleManager.ParseIdleInfoFromRow", pr);
            if (sMiMissing.Length > 0) { AppliedLog().Add("moodidleflavor: not found: " + sMiMissing); return; }
            sMiRow = (MiRowFn)Delegate.CreateDelegate(typeof(MiRowFn), pr);
            Patch(typeof(IdleManager), "ParseMoodIdleData", new Type[] { typeof(XmlDbData) }, typeof(BMiHook), "ParseHook", "ParseOrig");
        }

        // Pure: the flavour a row belongs to: its own, else the last named one (EA: Fulfilled, the row dropped).
        internal static MoodFlavor MiFlavor(bool on, bool named, MoodFlavor own, MoodFlavor last) { return named ? own : (on ? last : MoodFlavor.Fulfilled); }

        // EA's loop with the flavour remembered; appendOnly adds only the continuation rows (table parsed by EA already).
        static void MiParse(XmlDbData data, bool appendOnly)
        {
            XmlDbTable t = null;
            data.Tables.TryGetValue("MoodIdle", out t);
            if (t == null) return;
            MoodFlavor last = MoodFlavor.Fulfilled;
            bool haveLast = false;  // a flag, not a Fulfilled sentinel: rows after a Fulfilled row join it too
            foreach (XmlDbRow row in t.Rows)
            {
                string text = row.GetString("MoodFlavor");
                bool named = text != string.Empty;
                MoodFlavor f = MoodFlavor.Fulfilled;
                if (named) ParserFunctions.TryParseEnum(text, out f, MoodFlavor.Fulfilled);
                if (!named && !haveLast) continue;  // rows before the first named one have no flavour (EA drops them too)
                MoodFlavor g = MiFlavor(true, named, f, last);
                if (named)
                {
                    if (!appendOnly) IdleManager.sMoodIdleAnimations.Add((long)g, new IdleAnimationInfo(sMiRow(row, true)));
                    last = g;
                    haveLast = true;
                    continue;
                }
                IdleAnimationInfo info;
                if (IdleManager.sMoodIdleAnimations.TryGetValue((long)g, out info)) { info.AddAnimation(sMiRow(row, true)); PatchCounters()[3061]++; }
            }
        }

        static int MiCountDropped(XmlDbData data)
        {
            XmlDbTable t = null;
            data.Tables.TryGetValue("MoodIdle", out t);
            if (t == null) return 0;
            int n = 0;
            bool seen = false;
            foreach (XmlDbRow row in t.Rows) { if (row.GetString("MoodFlavor") != string.Empty) seen = true; else if (seen) n++; }
            return n;
        }

        public class BMiHook
        {
            public static void ParseHook(XmlDbData data)
            {
                if (!ShortcutOn("moodidleflavor")) { try { PatchCounters()[3062] += MiCountDropped(data); } catch (Exception) { } ParseOrig(data); return; }
                try { MiParse(data, false); sMiOurs = true; }
                catch (Exception) { PatchCounters()[3063]++; }
            }

            public static void ParseOrig(XmlDbData data) { }
        }

        // World load: the table EA parsed before our hook gets its dropped rows once.
        internal static void MoodIdleFlavorWorldLoad()
        {
            if (sMiOurs || sMiRow == null || !ShortcutOn("moodidleflavor")) return;
            sMiOurs = true;
            try { MiParse(XmlDbData.ReadData("Idles"), true); }
            catch (Exception) { PatchCounters()[3063]++; }
        }
    }
}
