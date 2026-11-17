using System;
using System.Collections.Generic;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.InteractionsShared;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Counters;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Electronics;
using Sims3.Gameplay.Objects.Plumbing;
using Sims3.Gameplay.Opportunities;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Socializing;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;
using Sims3.UI.CAS;
using Sims3.UI.Controller;

namespace Ts3Fixes
{
    // callcensus (docs/584 §10, default off; log only, pass-through): how often the speedup candidates of docs/589 §3 and
    // docs/590 §3 really run, so only the hot ones get built. Each candidate is swapped for a hook that counts the call (and,
    // where the method does real work, times it with one running EA StopWatch, microseconds) and then runs EA's method
    // unchanged: same arguments, same result, same exceptions. Off, a hook only reads a cached flag (refreshed every 1024
    // calls) and calls EA's method. Once a sim-hour while on, ts3native.log gets one "callcensus:" line per candidate
    // that ran: calls since the first counted call, calls per sim-hour, and the mean microseconds per timed call.
    // Not hooked (and why): Motives / BuffManager.UpdateBuffs (motivebuf, ilp_buffbuf), IdleManager.TestIdleInfo (ilp_idleparams
    // patches its body), hourly / daily / menu-only methods, private nested classes (CarpoolManager's loop, Windows,
    // gardening, puddles) and single-interaction Definitions (docs/584 §10 lists them).
    public static partial class Fixes
    {
        static readonly string[] kCcNames = {
            "Food.LotHasUsableCounter", "Food.LotHasUsableStove", "Food.LotHasUsableMicrowave", "Food.LotHasUsableGrill",
            "Sink.GetBrushTeethScore", "BuffManager.HasAnyElement", "TraitManager.HasAnyElement", "SocialComponent.SocializeWithMultiplier",
            "Autonomy.CalculateScoreForSocial", "SocialComponent.FindSimOfRelationshipType", "Relationship.GetMiniRelationships",
            "Conversation.GetSocialsForSTC", "Conversation.IsSuitable", "ActionData.Test", "InteractionQueue.InsertInteraction",
            "ViewObjects.CreateRouteToObject", "TV.CountOfHumansWatching", "RabbitHole.GetRabbitHolesOfType",
            "ImprovedProtestSituation.FindProtest", "Terrain.ModifyRouteForSpecies", "SingingInfo.AllKnownCompositions",
            "BarAdvanced.CurrentSnackBowl", "PetGuard.IsBeingGuarded" };
        const int kCcStc = 11;
        static readonly long[] sCcCalls = new long[23];
        static readonly long[] sCcUs = new long[23];
        static readonly long[] sCcTimed = new long[23];
        static int sCcPoll;
        static bool sCcOn, sCcWarned;
        static long sCcSince = -1;
        static int sCcLines;
        static StopWatch sCcWatch;

        static void ApplyCallCensus()
        {
            Patch(typeof(Food), "LotHasUsableCounter", typeof(CcStatic), "Counter", "CounterOrig");
            Patch(typeof(Food), "LotHasUsableStove", typeof(CcStatic), "Stove", "StoveOrig");
            Patch(typeof(Food), "LotHasUsableMicrowave", typeof(CcStatic), "Microwave", "MicrowaveOrig");
            Patch(typeof(Food), "LotHasUsableGrill", typeof(CcStatic), "Grill", "GrillOrig");
            Patch(typeof(Sink), "GetBrushTeethScore", typeof(CcStatic), "BrushTeeth", "BrushTeethOrig");
            Patch(typeof(BuffManager), "HasAnyElement", new Type[] { typeof(BuffNames[]) }, typeof(CcBuffs), "AnyHook", "AnyOrig");
            Patch(typeof(TraitManager), "HasAnyElement", new Type[] { typeof(TraitNames[]) }, typeof(CcTraits), "AnyHook", "AnyOrig");
            Patch(typeof(SocialComponent), "SocializeWithMultiplier", new Type[] { typeof(Sim), typeof(Relationship) }, typeof(CcSocial), "MultHook", "MultOrig");
            Patch(typeof(Sims3.Gameplay.Autonomy.Autonomy), "CalculateScoreForSocial", new Type[] { typeof(SocialInteractionA.Definition), typeof(InteractionObjectPair) }, typeof(CcAutonomy), "ScoreHook", "ScoreOrig");
            Patch(typeof(SocialComponent), "FindSimOfRelationshipType", new Type[] { typeof(OpportunityRelationshipTypes), typeof(object[]), typeof(LongTermRelationshipTypes), typeof(Sim), typeof(CASAgeGenderFlags), typeof(bool) }, typeof(CcSocial), "FindHook", "FindOrig");
            Patch(typeof(Relationship), "GetMiniRelationships", new Type[] { typeof(SimDescription) }, typeof(CcStatic), "Mini", "MiniOrig");
            Patch(typeof(Conversation), "IsSuitable", typeof(CcStatic), "Suitable", "SuitableOrig");
            Patch(typeof(ActionData), "Test", new Type[] { typeof(Sim), typeof(Sim), typeof(bool), typeof(ActiveTopic), typeof(GreyedOutTooltipCallback).MakeByRefType(), typeof(ActionData.ChecksToSkip), typeof(bool) }, typeof(CcActionData), "TestHook", "TestOrig");
            Patch(typeof(InteractionQueue), "InsertInteraction", new Type[] { typeof(InteractionInstance), typeof(int) }, typeof(CcQueue), "InsertHook", "InsertOrig");
            Patch(typeof(ViewObjects), "CreateRouteToObject", typeof(CcStatic), "ViewRoute", "ViewRouteOrig");
            Patch(typeof(TV), "get_CountOfHumansWatchingTvOrPlayingVideoGames", typeof(CcTv), "CountHook", "CountOrig");
            Patch(typeof(RabbitHole), "GetRabbitHolesOfType", typeof(CcStatic), "Holes", "HolesOrig");
            Patch(typeof(ImprovedProtestSituation), "FindProtest", typeof(CcStatic), "Protest", "ProtestOrig");
            Patch(typeof(Terrain), "ModifyRouteForSpecies", typeof(CcStatic), "Species", "SpeciesOrig");
            Patch(typeof(SingingInfo), "get_AllKnownCompositions", typeof(CcSinging), "SongsHook", "SongsOrig");
            Patch(typeof(BarAdvanced), "get_CurrentSnackBowl", typeof(CcBar), "BowlHook", "BowlOrig");
            Patch(typeof(PetGuardSitutation.GuardObject), "IsBeingGuarded", typeof(CcStatic), "Guarded", "GuardedOrig");
        }

        // the cached switch: re-read every 1024 calls (the switches can arrive after the hooks went in)
        static bool CcOn()
        {
            if ((++sCcPoll & 1023) == 1) sCcOn = ShortcutOn("callcensus");
            return sCcOn;
        }
        static uint CcT0()
        {
            if (sCcWatch == null)
            {
                sCcWatch = StopWatch.Create(StopWatch.TickStyles.Microseconds);
                if (sCcWatch == null) return 0;
                sCcWatch.Start();
            }
            return sCcWatch.GetElapsedTime();
        }
        static void CcCount(int i)
        {
            sCcCalls[i]++;
            if (sCcSince < 0) sCcSince = SimClock.CurrentTicks;  // may run on the autonomy worker: no alarm from here
        }
        static void CcTime(int i, uint t0)
        {
            if (sCcWatch == null) return;
            sCcUs[i] += unchecked(sCcWatch.GetElapsedTime() - t0);  // uint wrap (71 min) handled by the unchecked difference
            sCcTimed[i]++;
        }
        // every world load (from UfTpCleanupWorldLoad, sim thread): the hourly log alarm; it writes only once a call was counted
        static AlarmHandle sCcAlarmH = AlarmHandle.kInvalidHandle;
        static void CallCensusWorldLoad()
        {
            if (AlarmManager.Global == null) return;
            if (sCcAlarmH != AlarmHandle.kInvalidHandle) AlarmManager.Global.RemoveAlarm(sCcAlarmH);
            sCcAlarmH = AlarmManager.Global.AddAlarmRepeating(1f, TimeUnit.Hours, delegate { try { CcLog(); } catch (Exception) { } }, 1f, TimeUnit.Hours, "Ts3Fixes.CallCensus", AlarmType.NeverPersisted, null);
        }
        static void CcLog()
        {
            if (sCcSince < 0 || sCcLines >= 3000) return;
            float hours = SimClock.ConvertFromTicks(SimClock.CurrentTicks - sCcSince, TimeUnit.Hours);
            if (sCcWatch == null && !sCcWarned) { sCcWarned = true; DiagLine("callcensus: no EA StopWatch, counts only"); }
            for (int i = 0; i < kCcNames.Length; i++)
            {
                if (sCcCalls[i] == 0) continue;
                string us = sCcTimed[i] > 0 ? ((double)sCcUs[i] / sCcTimed[i]).ToString("0.0") : "-";
                DiagLine("callcensus: " + kCcNames[i] + " calls " + sCcCalls[i] + " in " + hours.ToString("0.00") + " sim-h, per sim-hour "
                    + (hours > 0f ? (sCcCalls[i] / hours).ToString("0") : "-") + ", us/call " + us + ", ms total " + (sCcUs[i] / 1000));
                sCcLines++;
            }
        }

        public class CcStatic
        {
            public static bool Counter(Lot lot) { if (!CcOn()) return CounterOrig(lot); CcCount(0); uint t = CcT0(); try { return CounterOrig(lot); } finally { CcTime(0, t); } }
            public static bool CounterOrig(Lot lot) { return false; } // receives Food.LotHasUsableCounter's original header
            public static bool Stove(Lot lot) { if (!CcOn()) return StoveOrig(lot); CcCount(1); uint t = CcT0(); try { return StoveOrig(lot); } finally { CcTime(1, t); } }
            public static bool StoveOrig(Lot lot) { return false; } // receives Food.LotHasUsableStove's original header
            public static bool Microwave(Lot lot) { if (!CcOn()) return MicrowaveOrig(lot); CcCount(2); uint t = CcT0(); try { return MicrowaveOrig(lot); } finally { CcTime(2, t); } }
            public static bool MicrowaveOrig(Lot lot) { return false; } // receives Food.LotHasUsableMicrowave's original header
            public static bool Grill(Lot lot) { if (!CcOn()) return GrillOrig(lot); CcCount(3); uint t = CcT0(); try { return GrillOrig(lot); } finally { CcTime(3, t); } }
            public static bool GrillOrig(Lot lot) { return false; } // receives Food.LotHasUsableGrill's original header
            public static float BrushTeeth(Sim actor, InteractionObjectPair iop) { if (!CcOn()) return BrushTeethOrig(actor, iop); CcCount(4); uint t = CcT0(); try { return BrushTeethOrig(actor, iop); } finally { CcTime(4, t); } }
            public static float BrushTeethOrig(Sim actor, InteractionObjectPair iop) { return 0f; } // receives Sink.GetBrushTeethScore's original header
            public static List<IMiniRelationship> Mini(SimDescription s) { if (!CcOn()) return MiniOrig(s); CcCount(10); uint t = CcT0(); try { return MiniOrig(s); } finally { CcTime(10, t); } }
            public static List<IMiniRelationship> MiniOrig(SimDescription s) { return null; } // receives Relationship.GetMiniRelationships' original header
            public static bool Suitable(string action, Sim actor, Sim targetSim, Sim accordingTo, float currentLiking, LongTermRelationshipTypes currentLTR, ShortTermContextTypes currentSTC, float targetSimMood, SocialRuleLHS e, ActiveTopic topic)
            {
                if (!CcOn()) return SuitableOrig(action, actor, targetSim, accordingTo, currentLiking, currentLTR, currentSTC, targetSimMood, e, topic);
                CcCount(12); uint t = CcT0();
                try { return SuitableOrig(action, actor, targetSim, accordingTo, currentLiking, currentLTR, currentSTC, targetSimMood, e, topic); } finally { CcTime(12, t); }
            }
            public static bool SuitableOrig(string action, Sim actor, Sim targetSim, Sim accordingTo, float currentLiking, LongTermRelationshipTypes currentLTR, ShortTermContextTypes currentSTC, float targetSimMood, SocialRuleLHS e, ActiveTopic topic) { return false; } // receives Conversation.IsSuitable's original header
            public static Route ViewRoute(Sim actor, IGameObject objectToView, float fMin, float fMax) { if (!CcOn()) return ViewRouteOrig(actor, objectToView, fMin, fMax); CcCount(15); uint t = CcT0(); try { return ViewRouteOrig(actor, objectToView, fMin, fMax); } finally { CcTime(15, t); } }
            public static Route ViewRouteOrig(Sim actor, IGameObject objectToView, float fMin, float fMax) { return null; } // receives ViewObjects.CreateRouteToObject's original header
            public static List<RabbitHole> Holes(RabbitHoleType type) { if (!CcOn()) return HolesOrig(type); CcCount(17); uint t = CcT0(); try { return HolesOrig(type); } finally { CcTime(17, t); } }
            public static List<RabbitHole> HolesOrig(RabbitHoleType type) { return null; } // receives RabbitHole.GetRabbitHolesOfType's original header
            public static ImprovedProtestSituation Protest(Lot lot) { if (!CcOn()) return ProtestOrig(lot); CcCount(18); uint t = CcT0(); try { return ProtestOrig(lot); } finally { CcTime(18, t); } }
            public static ImprovedProtestSituation ProtestOrig(Lot lot) { return null; } // receives ImprovedProtestSituation.FindProtest's original header
            public static void Species(Route r, Sim routingSim) { if (!CcOn()) { SpeciesOrig(r, routingSim); return; } CcCount(19); uint t = CcT0(); try { SpeciesOrig(r, routingSim); } finally { CcTime(19, t); } }
            public static void SpeciesOrig(Route r, Sim routingSim) { } // receives Terrain.ModifyRouteForSpecies' original header
            public static bool Guarded(Sim actor, GameObject obj, GameObject ignorePet) { if (!CcOn()) return GuardedOrig(actor, obj, ignorePet); CcCount(22); uint t = CcT0(); try { return GuardedOrig(actor, obj, ignorePet); } finally { CcTime(22, t); } }
            public static bool GuardedOrig(Sim actor, GameObject obj, GameObject ignorePet) { return false; } // receives PetGuardSitutation.GuardObject.IsBeingGuarded's original header
        }

        public class CcBuffs : BuffManager
        {
            public bool AnyHook(params BuffNames[] guids) { if (CcOn()) CcCount(5); return AnyOrig(guids); }
            public bool AnyOrig(params BuffNames[] guids) { return false; } // receives BuffManager.HasAnyElement(BuffNames[])'s original header
        }
        public class CcTraits : TraitManager
        {
            public bool AnyHook(params TraitNames[] guids) { if (CcOn()) CcCount(6); return AnyOrig(guids); }
            public bool AnyOrig(params TraitNames[] guids) { return false; } // receives TraitManager.HasAnyElement(TraitNames[])'s original header
        }
        public class CcSocial : SocialComponent
        {
            public CcSocial() : base(null) { }
            public float MultHook(Sim other, Relationship r) { if (!CcOn()) return MultOrig(other, r); CcCount(7); uint t = CcT0(); try { return MultOrig(other, r); } finally { CcTime(7, t); } }
            public float MultOrig(Sim other, Relationship r) { return 0f; } // receives SocialComponent.SocializeWithMultiplier's original header
            public Sim FindHook(OpportunityRelationshipTypes simType, object[] data, LongTermRelationshipTypes targetLTR, Sim disallowedSim, CASAgeGenderFlags genderRequirement, bool mustLiveInCurrentWorld)
            {
                if (!CcOn()) return FindOrig(simType, data, targetLTR, disallowedSim, genderRequirement, mustLiveInCurrentWorld);
                CcCount(9); uint t = CcT0();
                try { return FindOrig(simType, data, targetLTR, disallowedSim, genderRequirement, mustLiveInCurrentWorld); } finally { CcTime(9, t); }
            }
            public Sim FindOrig(OpportunityRelationshipTypes simType, object[] data, LongTermRelationshipTypes targetLTR, Sim disallowedSim, CASAgeGenderFlags genderRequirement, bool mustLiveInCurrentWorld) { return null; } // receives SocialComponent.FindSimOfRelationshipType's original header
        }
        public class CcAutonomy : Sims3.Gameplay.Autonomy.Autonomy
        {
            public float ScoreHook(SocialInteractionA.Definition social, InteractionObjectPair iop) { if (!CcOn()) return ScoreOrig(social, iop); CcCount(8); uint t = CcT0(); try { return ScoreOrig(social, iop); } finally { CcTime(8, t); } }
            public float ScoreOrig(SocialInteractionA.Definition social, InteractionObjectPair iop) { return 0f; } // receives Autonomy.CalculateScoreForSocial's original header
        }
        public class CcActionData : ActionData
        {
            public CcActionData() : base(null, CommodityTypes.Undefined, ProductVersion.BaseGame, null, false) { }
            public InteractionTestResult TestHook(Sim actor, Sim other, bool isAutonomous, ActiveTopic topic, ref GreyedOutTooltipCallback greyedOutTooltipCallback, ChecksToSkip checksToSkip, bool needsCustomTest)
            {
                if (!CcOn()) return TestOrig(actor, other, isAutonomous, topic, ref greyedOutTooltipCallback, checksToSkip, needsCustomTest);
                CcCount(13); uint t = CcT0();
                try { return TestOrig(actor, other, isAutonomous, topic, ref greyedOutTooltipCallback, checksToSkip, needsCustomTest); } finally { CcTime(13, t); }
            }
            public InteractionTestResult TestOrig(Sim actor, Sim other, bool isAutonomous, ActiveTopic topic, ref GreyedOutTooltipCallback greyedOutTooltipCallback, ChecksToSkip checksToSkip, bool needsCustomTest) { return InteractionTestResult.Pass; } // receives ActionData.Test's original header
        }
        public class CcQueue : InteractionQueue
        {
            public bool InsertHook(InteractionInstance entry, int index) { if (!CcOn()) return InsertOrig(entry, index); CcCount(14); uint t = CcT0(); try { return InsertOrig(entry, index); } finally { CcTime(14, t); } }
            public bool InsertOrig(InteractionInstance entry, int index) { return false; } // receives InteractionQueue.InsertInteraction's original header
        }
        public abstract class CcTv : TV
        {
            public int CountHook() { if (CcOn()) CcCount(16); return CountOrig(); }
            public int CountOrig() { return 0; } // receives TV.CountOfHumansWatchingTvOrPlayingVideoGames' original getter
        }
        public class CcSinging : SingingInfo
        {
            public List<SingingComposition> SongsHook() { if (CcOn()) CcCount(20); return SongsOrig(); }
            public List<SingingComposition> SongsOrig() { return null; } // receives SingingInfo.AllKnownCompositions' original getter
        }
        public abstract class CcBar : BarAdvanced
        {
            public ISnackBowl BowlHook() { if (CcOn()) CcCount(21); return BowlOrig(); }
            public ISnackBowl BowlOrig() { return null; } // receives BarAdvanced.CurrentSnackBowl's original getter
        }
    }
}
