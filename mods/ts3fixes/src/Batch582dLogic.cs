using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.ActiveCareer;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.CelebritySystem;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Objects.Door;
using Sims3.Gameplay.Objects.PerformanceObjects;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // Batch 5 leads 7 and 8 (docs/582 B5 §9, §10; Roslyn docs/583): two EA methods replaced while their switch is on, each
    // our copy of EA's steps with only the bug changed. Switch off: EA's original runs. Both default off.
    public static partial class Fixes
    {
        // ---- B5 §9 talkoccupationchance (tuning kTalkOccupationChance) ----------------------------------------------------
        // [verified, decompiled] TalkAboutOccupation.Definition.AddInteractions (celebrity "Try to Impress... Talk About
        // Occupation" pie menu): EA's `float chance = 1 / (count + 1);` is integer division, so the "Unemployment" entry's
        // random chance is 0 whenever any occupation is left over (1 only when none is). Intent: 1/(count+1) as a float.
        // The copy keeps EA's order of steps, its RandomChance01 draw (EA draws even at chance 0), its sort (EA's own
        // ImpressMerticTuple list) and its loop limits. Household-visible: the player's pie menu may now offer Unemployment.
        // Also (batch 9, 2026-11-15 review): EA's best-choice cap tests `num > k` before adding, so the menu gets k + 1 best
        // choices (6 where the tuning says "at most 5"); the copy stops at k. GetRandomSeed is called only when some
        // occupation is left (it takes `% count`; with none left EA's loop adds nothing anyway).
        // Counters: [2886] menus built by the copy, [2887] Unemployment offered only through the fixed chance, [2888] errors.
        static MethodInfo sTaoOccPts, sTaoUnempPts, sTaoSeed;
        static ConstructorInfo sTaoTuple;
        static PropertyInfo sTaoScore, sTaoMetric;
        static readonly FieldInfo sTaoAges = typeof(ActiveCareerStaticData).GetField("ValidAges", kInstAll);
        static Type sTaoListType;
        static string sTaoMissing = "";

        internal static float TaoChance(bool on, int count) { return on ? 1f / (count + 1) : (float)(1 / (count + 1)); }

        static void ApplyTalkOccupationChance()
        {
            Type cm = typeof(CelebrityManager);
            sTaoOccPts = cm.GetMethod("GetCelebrityImpressPointsForTalkAboutOccupation", kStaticAll, null, new Type[] { typeof(Sim), typeof(Sim), typeof(OccupationNames), typeof(bool) }, null);
            sTaoUnempPts = cm.GetMethod("GetCelebrityImpressPointsForTalkAboutUnemployment", kStaticAll, null, new Type[] { typeof(Sim), typeof(Sim), typeof(bool) }, null);
            sTaoSeed = cm.GetMethod("GetRandomSeed", kStaticAll, null, new Type[] { typeof(Sim), typeof(Sim), typeof(int) }, null);
            Type tt = cm.Assembly.GetType("Sims3.Gameplay.CelebritySystem.ImpressMerticTuple");
            if (tt != null)
            {
                sTaoTuple = tt.GetConstructor(kInstAll, null, new Type[] { typeof(ulong), typeof(float) }, null);
                sTaoScore = tt.GetProperty("Score");
                sTaoMetric = tt.GetProperty("Metric");
                sTaoListType = typeof(List<>).MakeGenericType(tt);
            }
            sTaoMissing = ((sTaoOccPts == null ? "GetCelebrityImpressPointsForTalkAboutOccupation " : "") + (sTaoUnempPts == null ? "GetCelebrityImpressPointsForTalkAboutUnemployment " : "")
                + (sTaoSeed == null ? "GetRandomSeed " : "") + (sTaoAges == null ? "ActiveCareerStaticData.ValidAges " : "") + (sTaoTuple == null || sTaoScore == null || sTaoMetric == null ? "ImpressMerticTuple" : "")).Trim();
            if (sTaoMissing.Length > 0) { AppliedLog().Add("talkoccupationchance: not found: " + sTaoMissing); return; }
            Patch(typeof(TalkAboutOccupation.Definition), "AddInteractions", new Type[] { typeof(InteractionObjectPair), typeof(Sim), typeof(Sim), typeof(List<InteractionObjectPair>) }, typeof(BTaoHook), "AddHook", "AddOrig");
        }

        // Pure: room for another best choice? (EA, on false: `num > kBest` / `num > kMax`, one too many)
        internal static bool TaoRoomForBest(bool on, int num)
        {
            if (!on) return !(num > TalkAboutOccupation.kMaximumNumberOfBestChoiceOccupationsInPieMenu) && !(num > TalkAboutOccupation.kMaximumNumberOfOccupationsInPieMenu);
            return num < TalkAboutOccupation.kMaximumNumberOfBestChoiceOccupationsInPieMenu && num < TalkAboutOccupation.kMaximumNumberOfOccupationsInPieMenu;
        }

        static void TaoAdd(Sim actor, Sim target, List<InteractionObjectPair> results)
        {
            List<ulong> list = null;
            IList list2 = null;
            foreach (OccupationNames value in Enum.GetValues(typeof(OccupationNames)))
            {
                Occupation occ = CareerManager.GetStaticOccupation(value);
                if (occ == null || occ is School) continue;
                Career career = occ as Career;
                SimDescription sd = actor.SimDescription;
                if (career != null && !career.CareerAgeTest(sd)) continue;
                if (occ is Sims3.Gameplay.ActiveCareer.ActiveCareer)
                {
                    ActiveCareerStaticData data = Sims3.Gameplay.ActiveCareer.ActiveCareer.GetActiveCareerStaticData(value);
                    if (data == null || ((CASAgeGenderFlags)sTaoAges.GetValue(data) & sd.Age) == 0) continue;  // EA's internal ValidAges
                }
                if (list == null) list = new List<ulong>();
                list.Add((ulong)value);
                if (list2 == null) list2 = (IList)Activator.CreateInstance(sTaoListType);
                float pts = (float)sTaoOccPts.Invoke(null, new object[] { actor, target, value, false });
                list2.Add(sTaoTuple.Invoke(new object[] { (ulong)value, pts }));
            }
            if (list2 == null || list == null) return;
            sTaoListType.GetMethod("Sort", Type.EmptyTypes).Invoke(list2, null);  // EA's List<ImpressMerticTuple>.Sort
            int num = 0;
            for (int i = 0; i < list2.Count; i++)
            {
                if (!TaoRoomForBest(true, num)) break;
                object t = list2[i];
                if ((float)sTaoScore.GetValue(t, null) <= 0f) break;
                ulong metric = (ulong)sTaoMetric.GetValue(t, null);
                results.Add(new InteractionObjectPair(new TalkAboutOccupation.Definition((OccupationNames)metric), target));
                list.Remove(metric);
                num++;
            }
            int count = list.Count;
            float chance = TaoChance(true, count);  // the fix: EA's 1 / (count + 1) was integer division
            float unemp = (float)sTaoUnempPts.Invoke(null, new object[] { actor, target, false });
            if (unemp > 0f || RandomUtil.RandomChance01(chance))
            {
                if (unemp <= 0f && count > 0) PatchCounters()[2887]++;  // offered only through the fixed chance
                results.Add(new InteractionObjectPair(new TalkAboutOccupation.Definition(true), target));
                num++;
            }
            int left = count;
            if (count <= 0) return;
            int idx = (int)sTaoSeed.Invoke(null, new object[] { actor, target, count });
            while (num < TalkAboutOccupation.kMaximumNumberOfOccupationsInPieMenu && left > 0)
            {
                results.Add(new InteractionObjectPair(new TalkAboutOccupation.Definition((OccupationNames)list[idx]), target));
                idx = ++idx % count;
                num++;
                left--;
            }
        }

        public class BTaoHook
        {
            public void AddHook(InteractionObjectPair iop, Sim actor, Sim target, List<InteractionObjectPair> results)
            {
                if (!ShortcutOn("talkoccupationchance")) { AddOrig(iop, actor, target, results); return; }
                int before = results == null ? 0 : results.Count;
                try { TaoAdd(actor, target, results); PatchCounters()[2886]++; }
                catch (Exception)
                {
                    PatchCounters()[2888]++;
                    if (results != null && results.Count > before) results.RemoveRange(before, results.Count - before);
                    AddOrig(iop, actor, target, results);
                }
            }
            public void AddOrig(InteractionObjectPair iop, Sim actor, Sim target, List<InteractionObjectPair> results) { } // receives TalkAboutOccupation.Definition.AddInteractions
        }

        // ---- B5 §10 shelldoorsummon (tuning kShellDoorSummon) -------------------------------------------------------------
        // [verified, decompiled] BigLotShellDoor+SummonProprietor.Run (a Sim summons a show venue's proprietor back through the
        // shell door): EA queues the proprietor's TeleportMeHere, then `if (flag & queue.Add(exit))` with a non-short-circuit
        // `&`, so the exit interaction is queued even when the teleport was refused: the proprietor then walks out of a door
        // it was never brought to, and Run returns false with the exit left queued and no return-home alarm. Intent: `&&`,
        // the exit is queued only after the teleport. The copy keeps EA's other steps. Household-visible only as the
        // summoning Sim's interaction result; the proprietor is an NPC.
        // Counters: [2889] summons run by the copy, [2890] teleport refused (exit not queued; with the switch off EA queues it), [2891] errors.
        static FieldInfo sSdExit;
        static Type sSdType;
        static string sSdMissing = "";

        // Pure: is the exit interaction offered to the queue? EA: always; fixed: only when the teleport was queued.
        internal static bool SdQueueExit(bool on, bool teleportQueued) { return !on || teleportQueued; }

        static void ApplyShellDoorSummon()
        {
            sSdType = typeof(BigLotShellDoor).GetNestedType("SummonProprietor", BindingFlags.NonPublic | BindingFlags.Public);
            Type exit = typeof(BigLotShellDoor).GetNestedType("ProprietorExitBigLotShellDoor", BindingFlags.NonPublic | BindingFlags.Public);
            sSdExit = exit == null ? null : exit.GetField("Singleton", kStaticAll);
            sSdMissing = ((sSdType == null ? "BigLotShellDoor.SummonProprietor " : "") + (sSdExit == null ? "ProprietorExitBigLotShellDoor.Singleton" : "")).Trim();
            if (sSdMissing.Length > 0) { AppliedLog().Add("shelldoorsummon: not found: " + sSdMissing); return; }
            Patch(sSdType, "Run", Type.EmptyTypes, typeof(BSdHook), "RunHook", "RunOrig");
        }

        static bool SdRun(Interaction<Sim, BigLotShellDoor> ia)
        {
            Sim actor = ia.Actor;
            BigLotShellDoor door = ia.Target;
            if (!door.RouteToBigLotShellDoor(actor)) return false;
            Sim prop = door.LotCurrent.mProprietor;
            if (prop == null || prop.InteractionQueue == null) return false;
            ShowStage stage = door.LotCurrent.mProprietor.SimDescription.AssignedRole.RoleGivingObject as ShowStage;
            if (stage == null) return false;
            stage.ShellDoorForProprietorToReEnter = door;
            Terrain.TeleportMeHere.Definition def = new Terrain.TeleportMeHere.Definition(false);
            InteractionObjectPair iop = new InteractionObjectPair(def, Terrain.Singleton);
            InteractionInstanceParameters p = new InteractionInstanceParameters(iop, prop, new InteractionPriority(InteractionPriorityLevel.RequiredNPCBehavior), false, false);
            Terrain.TeleportMeHere tp = ((InteractionDefinition)def).CreateInstanceFromParameters(ref p) as Terrain.TeleportMeHere;
            tp.SetAndReserveDestination(door.GetSlotPosition(Door.RoutingSlots.Door1_Rear));
            bool teleportQueued = prop.InteractionQueue.Add(tp);
            InteractionInstance entry = ((InteractionDefinition)sSdExit.GetValue(null)).CreateInstance(door, prop, new InteractionPriority(InteractionPriorityLevel.RequiredNPCBehavior), false, false);
            if (!SdQueueExit(true, teleportQueued)) { PatchCounters()[2890]++; return false; }  // the fix: EA's `&` queued the exit anyway
            if (teleportQueued & prop.InteractionQueue.Add(entry))
            {
                if (stage.ProprietorReturnHomeAlarm != AlarmHandle.kInvalidHandle) door.RemoveAlarm(stage.ProprietorReturnHomeAlarm);
                stage.ProprietorReturnHomeAlarm = door.AddAlarmRepeating(10f, TimeUnit.Minutes, stage.CheckForProprietorToGoHome, "CheckForProprietorToGoHom", AlarmType.AlwaysPersisted);
                door.RouteSimAwayFromDoor(actor);
                return true;
            }
            return false;
        }

        public class BSdHook
        {
            public bool RunHook()
            {
                if (!ShortcutOn("shelldoorsummon")) return RunOrig();
                PatchCounters()[2889]++;
                try { return SdRun((Interaction<Sim, BigLotShellDoor>)(object)this); }
                catch (Exception) { PatchCounters()[2891]++; return false; }
            }
            public bool RunOrig() { return false; } // receives BigLotShellDoor+SummonProprietor.Run
        }
    }
}
