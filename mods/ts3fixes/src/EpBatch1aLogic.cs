using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Controllers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects;
using Sims3.Gameplay.Objects.CookingObjects;
using Sims3.Gameplay.Objects.Decorations;
using Sims3.Gameplay.Objects.FoodObjects;
using Sims3.Gameplay.Objects.HobbiesSkills;
using Sims3.Gameplay.Objects.Insect;
using Sims3.Gameplay.Objects.Miscellaneous;
using Sims3.Gameplay.Objects.Resort;
using Sims3.Gameplay.Objects.Vehicles;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // EP batch 1, part a (docs/587 leads 1-10; batch EP1). Every switch off by default. Each lead was diffed against simler90
    // Gameplay Core Mod UPDATE242 (docs/587 §1.6): none of the methods below is changed there. Shipped tuning was read
    // where a lead depends on it (tools/static/tuning-xml-dump.py). Checks: EpBatch1Checks.cs. Counter slots 1400-1499
    // (pinned block fixes-1400, docs/registry.md).
    public static partial class Fixes
    {
        const BindingFlags kEpStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- EP1 §2 suatgradebonus (tuning kSuatGradeBonus) ------------------------------------------------------------
        // [verified, decompiled + shipped tuning] UniversityWelcomeKit+TakeAptitudeTest.GetSUATScore scales the score by
        // kGradeBonus (shipped 1.3, 1.15, 1, 0.75, 0.5: best grade first). The school path indexes it with School.Grade
        // (A = 0 .. F = 4), which matches, but gates on `CurrentGrade() > Grade.A`, so A students get no bonus while B
        // students get x1.15. The academic path indexes the same array with AcademicsUtility.AcademicGrade (F = 0 .. A = 4),
        // the reverse order: an A GPA gets x0.5, a D GPA x0.75 instead of x1.15. The two paths cannot both be right, the
        // values only make sense best-first, and the gate skips exactly the top grade, so this is a code bug, not design
        // (the tunable comment "F,E,D,C,B,A" lists six letters for five values and is not usable as intent).
        // Fix: academic path indexed best-first (4 - grade; F still skipped as EA does); school path includes A.
        // Household-visible: SUAT scores, scholarships and credits for good students.
        // Counters: [3241] scores corrected, [3242] scores that differ with the switch off, [3243] errors.
        static Type sSuTest;
        static FieldInfo sSuBonus;

        // Pure: kGradeBonus index EA uses (-1: no multiplier). academic: AcademicGrade value; school: School.Grade value.
        internal static int SuEaIndex(bool academic, int grade) { return grade > 0 ? grade : -1; }
        // Pure: the index the tuning means (best grade first).
        internal static int SuFixIndex(bool academic, int grade)
        {
            if (academic) return grade > 0 && grade <= 4 ? 4 - grade : -1;
            return grade >= 0 && grade <= 4 ? grade : -1;
        }
        // Pure: the index used with the switch on / off.
        internal static int SuIndex(bool on, bool academic, int grade) { return on ? SuFixIndex(academic, grade) : SuEaIndex(academic, grade); }
        // Pure: does this score get a different multiplier than EA's? (the hook's decision)
        internal static bool SuCorrects(bool on, bool academic, int grade) { return SuIndex(on, academic, grade) != SuEaIndex(academic, grade); }
        // Pure: EA's clamp of the scaled score.
        internal static uint SuClamp(int num) { num = num > 400 ? 400 : num; return num < 100 ? 100u : (uint)num; }

        static void ApplySuatGradeBonus()
        {
            sSuTest = typeof(UniversityWelcomeKit).GetNestedType("TakeAptitudeTest", BindingFlags.NonPublic | BindingFlags.Public);
            sSuBonus = sSuTest == null ? null : sSuTest.GetField("kGradeBonus", kEpStatic);
            if (sSuBonus == null) { AppliedLog().Add("suatgradebonus: not found: UniversityWelcomeKit+TakeAptitudeTest.kGradeBonus"); return; }
            Patch(sSuTest, "GetSUATScore", new Type[] { typeof(int) }, typeof(BSuHook), "ScoreHook", "ScoreOrig");
        }

        public class BSuHook
        {
            public uint ScoreHook(int degreeName)
            {
                bool academic = false; int grade = -1; float[] bonus = null;
                try
                {
                    Sim a = ((InteractionInstance)(object)this).InstanceActor;
                    bonus = sSuBonus.GetValue(null) as float[];
                    AcademicDegree el = a == null || a.DegreeManager == null ? null : a.DegreeManager.GetElement((AcademicDegreeNames)degreeName);
                    if (el != null && el.CurrentGpaAsLetterGrade > AcademicsUtility.AcademicGrade.F) { academic = true; grade = (int)el.CurrentGpaAsLetterGrade; }
                    else if (a != null && a.School != null) grade = (int)a.School.CurrentGrade();
                }
                catch (Exception) { PatchCounters()[3243]++; return ScoreOrig(degreeName); }
                if (bonus == null || bonus.Length < 5) return ScoreOrig(degreeName);
                bool on = ShortcutOn("suatgradebonus");
                if (!SuCorrects(on, academic, grade))
                {
                    if (!on && SuCorrects(true, academic, grade)) PatchCounters()[3242]++;
                    return ScoreOrig(degreeName);
                }
                int ea = SuEaIndex(academic, grade), fix = SuIndex(on, academic, grade);
                PatchCounters()[3241]++;
                if (academic)
                {
                    // EA reads kGradeBonus[grade]: hand it the best-first values at that index for this one call (no yield inside)
                    float[] flipped = new float[bonus.Length];
                    for (int i = 0; i < bonus.Length; i++) flipped[i] = bonus[i];
                    flipped[ea] = bonus[fix];
                    sSuBonus.SetValue(null, flipped);
                    try { return ScoreOrig(degreeName); }
                    finally { sSuBonus.SetValue(null, bonus); }
                }
                // school A: EA applied no multiplier; its score starts at 100 and only grows, so below the 400 cap the
                // returned value is the unscaled score itself
                uint r = ScoreOrig(degreeName);
                return r >= 400 ? r : SuClamp((int)((float)(int)r * bonus[fix]));
            }
            public uint ScoreOrig(int degreeName) { return 0; } // UniversityWelcomeKit+TakeAptitudeTest.GetSUATScore
        }

    }
}
