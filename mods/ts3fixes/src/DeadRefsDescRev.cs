using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Academics;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Careers;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.EventSystem;
using Sims3.Gameplay.Interactions;
using Sims3.Gameplay.Skills;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;
using Sims3.SimIFace.CustomContent;

namespace Ts3Fixes
{
    // deadrefdescrev (docs/333 LC-7, docs/336 §10; default OFF, needs deadrefsweep + deadrefcadence): phase 4 of the sweep
    // (household descriptions: occupation / school Coworkers, FormerBoss, chess challenger, pregnancy dad) steps only the
    // descriptions that reference a description disposed or a Sim destroyed since its last run, from a reverse index
    // (DeadRefsCore.DescRevIndex: holder SimDescriptionId keyed by the referenced SimDescriptionId, or by the dad Sim's
    // ObjectId). A holder whose reference is already gone when noted is unkeyed and stepped on the next run.
    // Writers indexed [verified decompiled, docs/333 §7.3]: Occupation.AddCoworker (no override), School.SetCoworkersAndBoss
    // and AcademicCareer.SetUpStudent (direct Coworkers.Add on both sides: the owner and every coworker are re-noted),
    // Career.SetBoss (FormerBoss), Occupation.ImportContent (FormerBoss), LogicSkill.FindSuitableChessRankOpponent (the only
    // challenger writer; Batch591Logic invokes it, so the hook runs there too), Pregnancy.StartInternal / StartFromMerge
    // (the field SimDescription.Pregnancy is set there), Pregnancy.Continue + PetPregnancy.Continue
    // (mDad). Pregnancy.CheckForDad is not swapped (DeadRefs.cs invokes it by reflection, docs/242 swapinvoke): its EA caller
    // StartFromMerge is hooked, and the sweep re-notes the holder it called it for. Career clones start from the static template (no references) [inferred, I-601-2]. Mods write these public
    // fields without the hooks: the first run after a load and every kDeadRefIdleHours (4 sim hours) walk every description
    // and re-seed. Same removals as the full walk (the body is DescriptionHolders), household Sims included: an exact speedup.
    // deadrefdeltaverify (test only) walks every description on a delta run and counts misses (a description outside the
    // taken holders with a gone reference that no event of this run explains) and extras (a taken holder the walk never
    // reached although it is in a household of the walk); both must stay 0.
    // No leak: a disposed holder leaves at its event, Prune drops gone holders every run, any index reset clears the holder
    // map, reset at every load, capped at 2 x descriptions + 1024. Bookkeeping hooks run in every mode; the phase itself runs
    // only inside the sweep (live, not paused: DrWaitWhileFrozen).
    // [1744] delta runs, [1745] holders stepped by delta runs, [1746] index entries, [1747] holders held, [1748] hook notes,
    // [1749] full (seed) runs, [1750] verify descriptions checked, [1751] verify misses (must stay 0), [1752] verify extras
    // (must stay 0), [1753] unseeds by a hook call with the switch off, [1754] entries pruned, [1755] resets at load,
    // [1756] overflow resets, [1757] high-water entries.
    // LC-7b (docs/336 §11), no writer missed by construction:
    // - Occupation.ImportContent runs before CareerManager assigns mJob / mSchool, so its hook files the owner unkeyed (stepped
    //   and re-noted by the next delta run, which reads the real job) instead of reading the old job.
    // - kGotPregnant (EA StartInternal and NRaas Pregnancies.Start send it after the assignment) notes the mother.
    // - DescWriterScan: once per process, at the first load with the switch on, every loaded assembly outside EA's core that
    //   references Sims3GameplaySystems (mods, NRaas, ours less the hook classes and the allowed methods below) is scanned
    //   for IL writes of Occupation.Coworkers / FormerBoss, LogicSkill.mChessRankingNextChallenger, SimDescription.Pregnancy,
    //   Pregnancy.mDad. A kind with a foreign writer is untrusted: delta runs step it on every description of the walk
    //   (DescriptionHolders(d, kinds)), the taken holders get every kind. All four untrusted (or an unreadable assembly) =
    //   the full walk on every run, as with the switch off.
    // [1758]-[1761] foreign writer methods per kind (coworkers, formerboss, chess, pregnancy), [1762] untrusted kinds mask,
    // [1763] assemblies scanned, [1764] methods scanned, [1765] unreadable methods / types (fail-safe: every kind),
    // [1766] scan us, [1767] pregnancy-event notes, [1768] import holds, [1769] descriptions stepped for untrusted kinds only.
    // LC-7c (docs/336 §12): a token that does not resolve names no target (the instruction cannot run); a field name as a
    // string next to reflection by name makes that kind untrusted, InvokeMember / Reflection.Emit every kind; the pinned NRaas
    // writers (DescWriterScan.Pins) whose IL hash matches get a hook that notes their holder (or rely on kGotPregnant) and are
    // not scanned. A hash mismatch, a missing method or a refused hook leaves the method scanned (its kind untrusted).
    // [1770] methods with an unresolvable token, [1771] reflection-guard hits, [1772] pinned hooks installed, [1773] pin hash
    // mismatches, [1774] pinned hooks refused (already compiled / no Sim property), [1775] pins covered without a hook
    // (kGotPregnant, or a body that only removes).
    // LC-7d (docs/336 §13): CareerManager.mJob / mSchool stores count as coworkers + formerboss writes (a job object swapped in
    // brings its references); the NRaas ones are pinned: SafeStore.Dispose notes its Sim, the rest mark the index dirty and the
    // next run is a seed walk (DeadRefsDescRevPins.cs). [1776] seed walks forced by a dirty mark, [1777] dirty marks.
    // LC-7e (docs/336 §14): EA CareerManager.AssignJob / AssignSchool noted (the only EA job stores); [1778] sim hours this
    // load, [1779] most dirty-forced seeds in one sim hour.
    public static partial class Fixes
    {
        static readonly DescRevIndex<SimDescription> sDd = new DescRevIndex<SimDescription>();
        static bool sDdOpen, sDdDelta, sDdVerify;
        static int sDdUntrusted = -1; // DescWriterScan mask; -1 = not scanned yet (assemblies do not change after the start)
        static int sDdTravelKinds; // LC-7g: kinds of pinned writers that write only in a travel-based world (no hook)
        static DescWriterScan.Result sDdScan;
        static int sDdScanUs, sDdKinds; // sDdKinds: the untrusted mask of the open delta run
        static EventListener sDdPregListener;
        // ours, reviewed: the sweep body (removals only), the noted test writers, TakeARiskCheck (restores the list it swaps, then
        // notes), the static initializer (its GetField("mDad") is sPregDad, whose only SetValue is DescriptionHolders' removal)
        static readonly string[] kDdAllowed = { "Ts3Fixes.Fixes::DescriptionHolders", "Ts3Fixes.Fixes::DdSetPregnancy", "Ts3Fixes.Fixes::TakeARiskCheck", "Ts3Fixes.Fixes::.cctor" };
        static float sDdFullH;
        static Dictionary<ulong, bool> sDdTaken, sDdLeft, sDdDuringDesc, sDdDuringSim;

        static void ApplyDeadRefDescRev()
        {
            Patch(typeof(Occupation), "AddCoworker", new Type[] { typeof(SimDescription) }, typeof(DdAddCoworkerHook), "AddHook", "AddOrig");
            Patch(typeof(Career), "SetBoss", typeof(DdSetBossHook), "BossHook", "BossOrig");
            Patch(typeof(School), "SetCoworkersAndBoss", typeof(DdSchoolSetHook), "SetHook", "SetOrig");
            Patch(typeof(AcademicCareer), "SetUpStudent", typeof(DdStudentHook), "SetHook", "SetOrig");
            Patch(typeof(Occupation), "ImportContent", new Type[] { typeof(ResKeyTable), typeof(ObjectIdTable), typeof(IPropertyStreamReader) }, typeof(DdOccImportHook), "ImportHook", "ImportOrig");
            Patch(typeof(LogicSkill), "FindSuitableChessRankOpponent", typeof(DdChessHook), "FindHook", "FindOrig");
            Patch(typeof(Pregnancy), "StartInternal", new Type[] { typeof(Sim), typeof(SimDescription) }, typeof(DdPregStartHook), "StartHook", "StartOrig");
            Patch(typeof(Pregnancy), "StartFromMerge", typeof(DdPregMergeHook), "MergeHook", "MergeOrig");
            Patch(typeof(Pregnancy), "Continue", typeof(DdPregContinueHook), "ContinueHook", "ContinueOrig");
            Patch(typeof(PetPregnancy), "Continue", typeof(DdPetPregContinueHook), "ContinueHook", "ContinueOrig");
            // LC-7e (docs/336 §14): the only EA stores of an Occupation into CareerManager.mJob / mSchool (IL census, every
            // caller funnels through them: new job, rehire from QuitCareers, age-up / boarding school, university)
            Patch(typeof(CareerManager), "AssignJob", typeof(DdAssignJobHook), "JobHook", "JobOrig");
            Patch(typeof(CareerManager), "AssignSchool", typeof(DdAssignSchoolHook), "SchoolHook", "SchoolOrig");
        }

        static void DdGauges(int[] c)
        {
            c[1746] = sDd.Idx.Entries; c[1747] = sDd.Holders; c[1756] = sDd.Idx.Overflows; c[1757] = sDd.Idx.HighEntries;
            if (sDdScan != null)
            {
                for (int k = 0; k < 4; k++) c[1758 + k] = sDdScan.PerKind[k];
                c[1762] = sDdScan.Mask; c[1763] = sDdScan.Assemblies; c[1764] = sDdScan.Methods; c[1765] = sDdScan.Unreadable; c[1766] = sDdScanUs;
                c[1770] = sDdScan.Unresolved; c[1771] = sDdScan.Reflect;
            }
        }

        static void DdLoadReset()
        {
            int[] c = PatchCounters();
            sDd.Reset(); sDdOpen = sDdDelta = false; sDdTaken = sDdLeft = sDdDuringDesc = sDdDuringSim = null;
            c[1755]++; DdGauges(c);
            sDdDirty = false; sDdStartH = -1f; sDdDirtyHour = -1;
            // under the load screen, not in the first live sweep; never inside a travel load (LC-7g: the scan installs pinned
            // hooks; EA's InWorldState.Startup imports the travellers after this, inside one catch-all): the first sweep scans
            if (ShortcutOn("deadrefdescrev") && !Sims3.Gameplay.GameStates.IsTravelling) DdUntrusted();
        }

        // The untrusted kinds, scanned on first use.
        static int DdUntrusted()
        {
            if (sDdUntrusted >= 0) { DdScanFlush(); return sDdUntrusted; }
            int[] c = PatchCounters();
            StopWatch sw = StopWatch.Create(StopWatch.TickStyles.Microseconds);
            sw.Start();
            DescWriterScan.Result r = new DescWriterScan.Result();
            try
            {
                Dictionary<string, int> fields = new Dictionary<string, int>();
                fields["Sims3.Gameplay.Careers.Occupation::Coworkers"] = DescWriterScan.KCoworkers;
                fields["Sims3.Gameplay.Careers.Occupation::FormerBoss"] = DescWriterScan.KFormerBoss;
                fields["Sims3.Gameplay.Skills.LogicSkill::mChessRankingNextChallenger"] = DescWriterScan.KChess;
                fields["Sims3.Gameplay.CAS.SimDescription::Pregnancy"] = DescWriterScan.KPregnancy;
                fields["Sims3.Gameplay.ActorSystems.Pregnancy::mDad"] = DescWriterScan.KPregnancy;
                fields["Sims3.Gameplay.Careers.CareerManager::mJob"] = DescWriterScan.KCoworkers | DescWriterScan.KFormerBoss; // LC-7d: a job object swapped in
                fields["Sims3.Gameplay.Careers.CareerManager::mSchool"] = DescWriterScan.KCoworkers | DescWriterScan.KFormerBoss; // brings its Coworkers / FormerBoss
                Dictionary<string, bool> lists = new Dictionary<string, bool>();
                lists["Sims3.Gameplay.Careers.Occupation::Coworkers"] = true;
                Dictionary<string, bool> allowed = new Dictionary<string, bool>();
                foreach (string k in kDdAllowed) allowed[k] = true;
                Assembly own = typeof(Fixes).Assembly;
                DescWriterScan.Core = delegate(Assembly x) { foreach (string g in kGameModules) if (string.Equals(g, x.GetName().Name, StringComparison.OrdinalIgnoreCase)) return true; return false; };
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n = a.GetName().Name;
                    bool core = false;
                    foreach (string g in kGameModules) if (string.Equals(g, n, StringComparison.OrdinalIgnoreCase)) core = true;
                    if (core) continue; // EA's writers: the hooks above (docs/336 §10.2 census)
                    bool refsGameplay = false;
                    foreach (AssemblyName rn in a.GetReferencedAssemblies()) if (rn.Name == "Sims3GameplaySystems") refsGameplay = true;
                    if (!refsGameplay) continue; // cannot name the fields
                    Dictionary<string, bool> al = allowed;
                    if (a != own)
                    {
                        List<string> mism = new List<string>();
                        foreach (KeyValuePair<DescWriterScan.Pin, MethodBase> pm in DescWriterScan.MatchPins(a, DescWriterScan.Pins, mism))
                        {
                            bool travel = pm.Key.Hook == "TravelWorld"; // LC-7g: no swap, untrusted in travel-based worlds (DdGather)
                            if (travel) sDdTravelKinds |= pm.Key.Kind;
                            else if (pm.Key.Hook != null && !DdPinHook(pm.Key, pm.Value)) { c[1774]++; DdScanLine("deadrefdescrev pin refused: " + n + " " + pm.Key.Key); continue; }
                            c[pm.Key.Hook != null && !travel ? 1772 : 1775]++;
                            if (al == allowed) al = new Dictionary<string, bool>(allowed);
                            DescWriterScan.Cover(al, pm.Value);
                        }
                        c[1773] += mism.Count;
                        foreach (string m in mism) DdScanLine("deadrefdescrev pin hash mismatch: " + m);
                    }
                    // our hook classes are skipped: their stubs hold EA's original bodies (and their GetMethods was the 0x58D9B6 assert, docs/242)
                    DescWriterScan.ScanAssembly(a, a == own ? (Predicate<Type>)delegate(Type t) { return sSwapsByHook.ContainsKey(t); } : null,
                        al, DescWriterScan.ReflectionBody, fields, lists, r, 40);
                }
            }
            catch (Exception ex) { r.Unreadable++; r.Add(DescWriterScan.KAll, "scan failed: " + ex.GetType().Name, 40); }
            sDdScanUs = (int)sw.GetElapsedTime();
            sw.Dispose();
            sDdScan = r; sDdUntrusted = r.Mask;
            DdScanLine("deadrefdescrev scan: " + r.Assemblies + " assemblies, " + r.Methods + " methods, " + r.Unreadable + " unreadable, " + r.Unresolved + " unresolvable-token, "
                + r.Reflect + " reflection, pins " + c[1772] + " hooked / " + c[1775] + " no-hook / " + c[1773] + " mismatch / " + c[1774] + " refused, " + sDdScanUs + " us; untrusted " + DescWriterScan.Kinds(r.Mask));
            foreach (string l in r.Log) DdScanLine("deadrefdescrev scan: " + l);
            DdScanFlush();
            DdGauges(c);
            return sDdUntrusted;
        }

        // LC-7f: the scan's lines also go to ts3native.log (DiagLine; each finding names its kinds, assembly, Type::Method and
        // the IL offset, opcode, field and member it writes through). Kept until the diag channel takes them (the scan runs
        // under the load screen), retried on each DdUntrusted call; one scan per process, so at most 2 + 40 + pins lines.
        static readonly List<string> sDdScanLines = new List<string>();
        static void DdScanLine(string l) { ShortcutLog(l); sDdScanLines.Add(l); }
        static void DdScanFlush() { while (sDdScanLines.Count > 0 && DiagLine(sDdScanLines[0])) sDdScanLines.RemoveAt(0); }

        static bool sDdDirty; // LC-7d: the next phase-4 run seeds (walks every description)
        static float sDdStartH = -1f;
        static int sDdDirtyHour = -1, sDdDirtyInHour;

        static void DdDirty()
        {
            if (!sDd.Live) return; // not seeded yet: the next run seeds anyway
            sDdDirty = true; PatchCounters()[1777]++;
        }

        static PropertyInfo sDdLostFatherSim; // AttachLostFathersScenario.Sim (SimScenario): the mother whose pregnancy it writes

        // LC-7c: the hook for a pinned NRaas writer whose hash matched. Hook classes name NRaas types as object (as CompatResolve's).
        static bool DdPinHook(DescWriterScan.Pin p, MethodBase m)
        {
            Type hook;
            switch (p.Hook)
            {
                case "SetBoss": hook = typeof(DdPinSetBossHook); break;
                case "Chess": hook = typeof(DdPinChessHook); break;
                case "LostFather":
                    try { sDdLostFatherSim = m.DeclaringType.GetProperty("Sim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch (Exception) { sDdLostFatherSim = null; }
                    if (sDdLostFatherSim == null || sDdLostFatherSim.PropertyType != typeof(SimDescription) || sDdLostFatherSim.GetGetMethod(true) == null) return false;
                    hook = typeof(DdPinLostFatherHook); break;
                case "BabyCleanup": hook = typeof(DdPinBabyCleanupHook); break;
                case "CopyPregnancy": hook = typeof(DdPinCopyPregHook); break;
                case "SafeStore":
                    FieldInfo ms = m.DeclaringType.GetField("mSim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ms == null || ms.FieldType != typeof(SimDescription)) return false;
                    hook = DdPinSlot(m);
                    if (hook != null) sDdSafeSim[m.DeclaringType] = ms;
                    break;
                case "Dirty": hook = DdPinSlot(m); break;
                default: return false;
            }
            if (hook == null) return false; // no free slot of that shape
            ParameterInfo[] ps = m.GetParameters();
            Type[] args = new Type[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ps[i].ParameterType;
            return Patch(m.DeclaringType, m.Name, args, hook, "Hook", "Orig");
        }

        // NewBossScenario.SetBoss(Career, SimDescription): job.FormerBoss
        public static class DdPinSetBossHook
        {
            public static void Hook(Career job, SimDescription boss) { try { Orig(job, boss); } finally { if (job != null) DdNote(job.OwnerDescription); } }
            public static void Orig(Career job, SimDescription boss) { }
        }

        // ChessPushScenario.FindNextOpponent(SimDescription sim): sim's LogicSkill challenger
        public class DdPinChessHook
        {
            public SimDescription Hook(SimDescription sim) { try { return Orig(sim); } finally { DdNote(sim); } }
            public SimDescription Orig(SimDescription sim) { return null; }
        }

        // AttachLostFathersScenario.PrivateUpdate(ScenarioFrame): Sim.Pregnancy.mDad
        public class DdPinLostFatherHook
        {
            public bool Hook(object frame) { try { return Orig(frame); } finally { DdPinNoteSim(this); } }
            public bool Orig(object frame) { return false; }
        }

        static void DdPinNoteSim(object scenario)
        {
            PropertyInfo pi = sDdLostFatherSim;
            if (pi == null) return;
            try { DdNote(pi.GetValue(scenario, null) as SimDescription); } catch (Exception) { PatchCounters()[410]++; }
        }

        // MaleHaveBabyHomeEx.Cleanup(): Actor.SimDescription.Pregnancy set to null and back
        public class DdPinBabyCleanupHook
        {
            public void Hook()
            {
                try { Orig(); }
                finally { InteractionInstance ii = (object)this as InteractionInstance; Sim a = ii != null ? ii.InstanceActor : null; if (a != null) DdNote(a.SimDescription); }
            }
            public void Orig() { }
        }

        // PregnancyProxy.CopyPregnancy(Pregnancy dst, Pregnancy src): dst.mDad (dst's mother holds it once assigned)
        public static class DdPinCopyPregHook
        {
            public static void Hook(Pregnancy dst, Pregnancy src) { try { Orig(dst, src); } finally { if (dst != null && dst.Mom != null) DdNote(dst.Mom.SimDescription); } }
            public static void Orig(Pregnancy dst, Pregnancy src) { }
        }

        // kSimDestroyed / kSimDescriptionDisposed: the holders of that Sim / description are due; a disposed holder leaves
        static void DdMarkEvent(Sims3.Gameplay.EventSystem.Event e)
        {
            Sim s = e != null ? e.TargetObject as Sim : null;
            if (s != null) { sDd.Idx.MarkSim(s.ObjectId.Value); if (sDdDuringSim != null) sDdDuringSim[s.ObjectId.Value] = true; }
            SimDescriptionEvent de = e as SimDescriptionEvent;
            if (de != null && de.SimDescription != null)
            {
                ulong id = de.SimDescription.SimDescriptionId;
                sDd.Drop(id); sDd.Idx.MarkDesc(id);
                if (sDdDuringDesc != null) sDdDuringDesc[id] = true;
            }
        }

        // the walk's members (Household.EverySimDescription): a household in the list, not the previous travellers
        static bool DdInWalk(SimDescription d)
        {
            Household h = d.Household;
            return h != null && !h.IsPreviousTravelerHousehold && Household.sHouseholdList.Contains(h) && h.AllSimDescriptions.Contains(d);
        }

        static void DdKey(SimDescription r, ulong id, ref bool any)
        {
            if (r == null) return;
            any = true;
            if (DescGone(r)) sDd.Idx.NoteUnkeyed(id); else sDd.Idx.NoteDesc(r.SimDescriptionId, id);
        }

        // Keys of every reference d holds now (the fields DescriptionHolders reads).
        static void DdNoteRefs(SimDescription d)
        {
            ulong id = d.SimDescriptionId;
            bool any = false;
            CareerManager cm = d.CareerManager;
            if (cm != null)
            {
                foreach (Occupation job in new Occupation[] { cm.Occupation, cm.School })
                {
                    if (job == null) continue;
                    if (job.Coworkers != null)
                        foreach (SimDescription w in job.Coworkers) { if (w == null) { sDd.Idx.NoteUnkeyed(id); any = true; } else DdKey(w, id, ref any); } // a null entry is "gone" to DescGone
                    DdKey(job.FormerBoss, id, ref any);
                }
            }
            if (d.SkillManager != null)
            {
                LogicSkill logic = d.SkillManager.GetSkill<LogicSkill>(SkillNames.Logic);
                if (logic != null) DdKey(logic.mChessRankingNextChallenger, id, ref any);
            }
            Pregnancy preg = d.Pregnancy;
            Sim dad = preg != null && sPregDad != null ? sPregDad.GetValue(preg) as Sim : null;
            if (dad != null)
            {
                any = true;
                if (dad.HasBeenDestroyed) sDd.Idx.NoteUnkeyed(id); else sDd.Idx.NoteSim(dad.ObjectId.Value, id);
            }
            if (any) sDd.Hold(id, d);
        }

        // Read-only: references of d that DescriptionHolders would clear now, less those an event of this run explains.
        // kinds: the trusted ones (an untrusted kind is stepped on every description anyway)
        static int DdGoneRefs(SimDescription d, int kinds)
        {
            int n = 0;
            CareerManager cm = d.CareerManager;
            if (cm != null)
                foreach (Occupation job in new Occupation[] { cm.Occupation, cm.School })
                {
                    if (job == null) continue;
                    if ((kinds & DescWriterScan.KCoworkers) != 0 && job.Coworkers != null) foreach (SimDescription w in job.Coworkers) if (DdGoneUnexplained(w)) n++;
                    if ((kinds & DescWriterScan.KFormerBoss) != 0 && job.FormerBoss != null && DdGoneUnexplained(job.FormerBoss)) n++;
                }
            if ((kinds & DescWriterScan.KChess) != 0 && d.SkillManager != null)
            {
                LogicSkill logic = d.SkillManager.GetSkill<LogicSkill>(SkillNames.Logic);
                if (logic != null && logic.mChessRankingNextChallenger != null && DdGoneUnexplained(logic.mChessRankingNextChallenger)) n++;
            }
            Pregnancy preg = (kinds & DescWriterScan.KPregnancy) != 0 ? d.Pregnancy : null;
            Sim dad = preg != null && sPregDad != null ? sPregDad.GetValue(preg) as Sim : null;
            if (dad != null && dad.HasBeenDestroyed && !(sDdDuringSim != null && sDdDuringSim.ContainsKey(dad.ObjectId.Value))) n++;
            return n;
        }
        static bool DdGoneUnexplained(SimDescription r)
        {
            return DescGone(r) && !(r != null && sDdDuringDesc != null && sDdDuringDesc.ContainsKey(r.SimDescriptionId));
        }

        // Phase 4's items (DeadRefSweepRun). Switch off: every description, as before. On: the first run after a load, an
        // unseeded index or the 4 h floor walk every description and seed; else only the taken holders (verify: every
        // description, the taken set compared in DdStep / DdPhaseEnd).
        static List<object> DdGather(bool cad)
        {
            int[] c = PatchCounters();
            if (!cad || !ShortcutOn("deadrefdescrev") || DdUntrusted() == DescWriterScan.KAll) // every kind has a foreign writer: the full walk
            {
                if (sDd.Live || sDd.Holders > 0) { sDd.Reset(); DdGauges(c); }
                return ToObjects(Household.EverySimDescription());
            }
            sDdVerify = ShortcutOn("deadrefdeltaverify");
            sDdOpen = true;
            sDdTaken = sDdLeft = null;
            float nowH = SimClock.ElapsedTime(TimeUnit.Hours);
            if (sDdStartH < 0f) sDdStartH = nowH;
            c[1778] = (int)(nowH - sDdStartH); // LC-7e: sim hours this load, for dirty seeds per sim hour
            bool full = !sDd.Idx.Seeded || nowH - sDdFullH >= kDeadRefIdleHours;
            if (!full && sDd.OverCap(DdDescCount())) full = true; // bigger than the world explains: walk and seed again
            if (!full && sDdDirty) // LC-7d: a pinned job-swap writer ran since the last run
            {
                full = true; c[1776]++;
                int h = (int)nowH;
                if (h != sDdDirtyHour) { sDdDirtyHour = h; sDdDirtyInHour = 0; }
                if (++sDdDirtyInHour > c[1779]) c[1779] = sDdDirtyInHour; // most in one sim hour
            }
            sDdDirty = false;
            if (full)
            {
                sDdDelta = false; sDdDuringDesc = sDdDuringSim = null;
                sDd.BeginSeed(); c[1749]++; DdGauges(c);
                return ToObjects(Household.EverySimDescription());
            }
            sDdDelta = true; c[1744]++;
            sDdKinds = sDdUntrusted | (sDdTravelKinds != 0 && GameUtils.IsAnyTravelBasedWorld() ? sDdTravelKinds : 0);
            c[1754] += sDd.Prune(DescGone);
            int fromKeys, fromUnkeyed;
            sDdTaken = sDd.Idx.TakeHolders(out fromKeys, out fromUnkeyed);
            sDdDuringDesc = new Dictionary<ulong, bool>(); sDdDuringSim = new Dictionary<ulong, bool>();
            List<object> l = new List<object>();
            List<SimDescription> walk = null;
            if (sDdVerify) sDdLeft = new Dictionary<ulong, bool>();
            foreach (ulong id in sDdTaken.Keys)
            {
                SimDescription d = sDd.Get(id);
                if (d == null || DescGone(d)) continue;
                if (!DdInWalk(d)) { DdNoteRefs(d); continue; } // outside the walk: not stepped, its keys kept for later
                if (sDdLeft != null) sDdLeft[id] = true; else l.Add(d);
            }
            if (!sDdVerify) c[1745] += l.Count;
            // verify: every description, compared in DdStep; untrusted kinds: every description, the others for those kinds only
            if (sDdVerify || sDdKinds != 0) { walk = Household.EverySimDescription(); l = ToObjects(walk); }
            DdGauges(c);
            return l;
        }

        // descriptions in the walk's households, without building the list
        static int DdDescCount()
        {
            int n = 0;
            foreach (Household h in Household.sHouseholdList) if (h != null && !h.IsPreviousTravelerHousehold) n += h.AllSimDescriptions.Count;
            return n;
        }

        // Phase 4's step: verify compare, DescriptionHolders, then what the description still holds is noted again.
        static void DdStep(SimDescription d)
        {
            if (sDdOpen && sDdDelta && sDdVerify && d != null && sDdTaken != null)
            {
                int[] c = PatchCounters();
                ulong id = d.SimDescriptionId;
                c[1750]++;
                if (sDdLeft != null) sDdLeft.Remove(id);
                try
                {
                    if (!sDdTaken.ContainsKey(id) && !DescGone(d))
                    {
                        int g = DdGoneRefs(d, DescWriterScan.KAll & ~sDdKinds);
                        if (g > 0 && c[1751]++ < 5) ShortcutLog("deadrefdescrev verify: " + d.FullName + " " + id.ToString("X") + " holds " + g + " gone references outside the taken holders (unhooked writer)");
                    }
                }
                catch (Exception) { c[410]++; } // the step below still runs, as on a full walk
            }
            if (sDdOpen && sDdDelta && !sDdVerify && sDdKinds != 0 && d != null && sDdTaken != null && !sDdTaken.ContainsKey(d.SimDescriptionId))
            {
                PatchCounters()[1769]++;
                DescriptionHolders(d, sDdKinds); // not a holder due: only the kinds a foreign writer can fill unseen
                return;
            }
            DescriptionHolders(d);
            if (sDdOpen && d != null && !DescGone(d) && sDd.Live) DdNoteRefs(d);
        }

        // phase 4 finished (DeadRefSweepRun)
        static void DdPhaseEnd()
        {
            if (!sDdOpen) return;
            int[] c = PatchCounters();
            if (sDd.Idx.Seeding) { sDd.Idx.EndSeed(); sDdFullH = SimClock.ElapsedTime(TimeUnit.Hours); }
            if (sDdLeft != null)
                foreach (ulong id in sDdLeft.Keys)
                {
                    SimDescription d = sDd.Get(id);
                    if (d != null && !DescGone(d) && DdInWalk(d) && c[1752]++ < 5) ShortcutLog("deadrefdescrev verify: taken holder " + id.ToString("X") + " not reached by the walk");
                }
            sDdOpen = sDdDelta = false; sDdKinds = 0; sDdTaken = sDdLeft = sDdDuringDesc = sDdDuringSim = null;
            DdGauges(c);
        }

        static bool DdDeltaOpen() { return sDdOpen && sDdDelta && !sDdVerify; }

        // the pass stopped inside phase 4: a seed is incomplete, a delta run lost the keys it took; the next run walks again
        static void DdStopped()
        {
            if (!sDdOpen) return;
            sDd.Reset(); sDdOpen = sDdDelta = false; sDdKinds = 0; sDdTaken = sDdLeft = sDdDuringDesc = sDdDuringSim = null;
            DdGauges(PatchCounters());
        }

        // A writer touched d's references (hooks).
        static void DdNote(SimDescription d)
        {
            if (d == null || !sDd.Live) return;
            int[] c = PatchCounters();
            if (!ShortcutOn("deadrefdescrev") || !DrCadence()) { sDd.Reset(); c[1753]++; DdGauges(c); return; } // the index misses this write
            try { if (!DescGone(d)) { DdNoteRefs(d); c[1748]++; } } catch (Exception) { c[410]++; }
            DdGauges(c);
        }

        // G1: a writer whose result d does not hold yet (Occupation.ImportContent inside CareerManager.ImportJobContent): d is
        // filed unkeyed, so the next delta run steps it and notes the keys it holds by then.
        static void DdNoteHeld(SimDescription d)
        {
            if (d == null || !sDd.Live) return;
            int[] c = PatchCounters();
            if (!ShortcutOn("deadrefdescrev") || !DrCadence()) { sDd.Reset(); c[1753]++; DdGauges(c); return; }
            if (!DescGone(d)) { sDd.Idx.NoteUnkeyed(d.SimDescriptionId); sDd.Hold(d.SimDescriptionId, d); c[1768]++; }
            DdGauges(c);
        }

        // kGotPregnant (DeadRefs.cs listener): the mother (actor) and the holder of the new pregnancy
        static ListenerAction OnDdPregEvent(Sims3.Gameplay.EventSystem.Event e)
        {
            try
            {
                Sim mom = e != null ? e.Actor as Sim : null;
                if (mom != null && sDd.Live) { DdNote(mom.SimDescription); PatchCounters()[1767]++; }
                PregnancyEvent pe = e as PregnancyEvent;
                Sim m2 = pe != null && pe.Pregnancy != null ? pe.Pregnancy.Mom : null;
                if (m2 != null && m2 != mom) DdNote(m2.SimDescription);
            }
            catch (Exception) { PatchCounters()[410]++; }
            return ListenerAction.Keep;
        }

        // T1: our test code sets a pregnancy through here (noted, and allowed by the writer scan)
        internal static void DdSetPregnancy(SimDescription d, Pregnancy p) { d.Pregnancy = p; DdNote(d); }

        // d and every coworker of d (writers that add on both sides)
        static void DdNoteBoth(SimDescription d)
        {
            if (d == null || !sDd.Live) return;
            DdNote(d);
            CareerManager cm = d.CareerManager;
            if (cm == null) return;
            foreach (Occupation job in new Occupation[] { cm.Occupation, cm.School })
                if (job != null && job.Coworkers != null)
                    foreach (SimDescription w in new List<SimDescription>(job.Coworkers)) DdNote(w);
        }

        static SimDescription DdOwner(object job) { Occupation o = job as Occupation; return o != null ? o.OwnerDescription : null; }

        // Repro lc7-modwrite (docs/336 §11.5, test only, NPCs only): a FormerBoss written by reflection (a writer no hook and no
        // scan sees, as a mod would), a pregnancy started through NRaas Pregnancies.Start (its own `Pregnancy =` write), then the
        // referenced Sim disposed. With the mods' kinds untrusted, verify must count no miss.
        static SimDescription sMwHolder, sMwRef, sMwMom;
        public static object DdModWriteCheck(string what)
        {
            Dictionary<string, object> r = new Dictionary<string, object>();
            int[] c = PatchCounters();
            try
            {
                if (what == "pick")
                {
                    sMwHolder = sMwRef = sMwMom = null;
                    foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
                    {
                        if (s == null || s.HasBeenDestroyed || !s.InWorld || IsPlayerSim(s) || s.IsPet || s.SimDescription == null) continue;
                        SimDescription d = s.SimDescription;
                        if (!(d.YoungAdult || d.Adult)) continue;
                        if (sMwRef == null && d.IsMale && s.Parent == null && !s.IsRouting) { sMwRef = d; continue; }
                        if (sMwMom == null && d.IsFemale && d.Pregnancy == null && s.InteractionQueue != null) { sMwMom = d; continue; }
                        if (sMwHolder == null && d.CareerManager != null && d.CareerManager.Occupation is Career) sMwHolder = d;
                    }
                    if (sMwRef == null || sMwMom == null || sMwHolder == null) { r["error"] = "no NPC ref / mom / holder with a career"; return r; }
                }
                else if (what == "boss")
                {
                    if (sMwHolder == null || sMwRef == null) { r["error"] = "pick first"; return r; }
                    // NRaas SP Career's NewBossScenario.SetBoss (pinned, LC-7c): boss = ref, then back, which leaves FormerBoss = ref
                    Career cj = sMwHolder.CareerManager.Occupation as Career;
                    MethodInfo sb = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = a.GetName().Name == "NRaasStoryProgressionCareer" ? a.GetType("NRaas.StoryProgressionSpace.Scenarios.Careers.NewBossScenario", false) : null;
                        if (t != null) sb = t.GetMethod("SetBoss", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, new Type[] { typeof(Career), typeof(SimDescription) }, null);
                    }
                    if (sb == null || cj == null) { r["error"] = "NRaas NewBossScenario.SetBoss not loaded or holder has no career"; return r; }
                    SimDescription was = cj.Boss;
                    sb.Invoke(null, new object[] { cj, sMwRef });
                    sb.Invoke(null, new object[] { cj, was == sMwRef ? null : was });
                }
                else if (what == "preg")
                {
                    if (sMwMom == null || sMwRef == null || sMwMom.CreatedSim == null) { r["error"] = "pick first"; return r; }
                    MethodInfo start = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = a.GetType("NRaas.CommonSpace.Helpers.Pregnancies", false);
                        MethodInfo m = t != null ? t.GetMethod("Start", new Type[] { typeof(Sim), typeof(SimDescription), typeof(bool) }) : null;
                        if (m != null && (start == null || a.GetName().Name == "NRaasWoohooer")) { start = m; r["nraas"] = a.GetName().Name; }
                    }
                    if (start == null) { r["error"] = "NRaas Pregnancies.Start not loaded"; return r; }
                    r["started"] = start.Invoke(null, new object[] { sMwMom.CreatedSim, sMwRef, false }) != null;
                }
                else if (what == "dispose")
                {
                    if (sMwRef == null) { r["error"] = "pick first"; return r; }
                    if (sMwRef.Household != null) sMwRef.Household.Remove(sMwRef);
                    sMwRef.Dispose(true, false, true);
                }
                else if (what != "state") { r["error"] = "modwrite <pick|boss|preg|dispose|state>"; return r; }
                Occupation job = sMwHolder != null && sMwHolder.CareerManager != null ? sMwHolder.CareerManager.Occupation : null;
                Pregnancy p = sMwMom != null ? sMwMom.Pregnancy : null;
                Sim dad = p != null && sPregDad != null ? sPregDad.GetValue(p) as Sim : null;
                r["holder"] = sMwHolder != null ? sMwHolder.FullName : null;
                r["ref"] = sMwRef != null ? sMwRef.FullName : null;
                r["ref_gone"] = sMwRef != null && DescGone(sMwRef);
                r["formerboss_is_ref"] = job != null && sMwRef != null && job.FormerBoss == sMwRef;
                r["mom_pregnant"] = p != null;
                r["dad_is_ref"] = dad != null && sMwRef != null && dad.SimDescription == sMwRef;
                r["dad_destroyed"] = dad != null && dad.HasBeenDestroyed;
                r["untrusted"] = DescWriterScan.Kinds(sDdUntrusted < 0 ? 0 : sDdUntrusted);
                r["verify_miss"] = c[1751]; r["verify_extra"] = c[1752]; r["kind_steps"] = c[1769]; r["preg_notes"] = c[1767];
            }
            catch (Exception ex) { r["error"] = ex.GetType().Name + ": " + ex.Message; }
            return r;
        }

        public class DdAddCoworkerHook
        {
            public void AddHook(SimDescription coworker) { AddOrig(coworker); DdNote(DdOwner(this)); }
            public void AddOrig(SimDescription coworker) { } // Occupation.AddCoworker
        }

        // LC-7e: CareerManager.AssignJob / AssignSchool store a job object that may already hold Coworkers / FormerBoss (a
        // rehire from QuitCareers): note its owner after the store (null = removal, nothing new held)
        public class DdAssignJobHook
        {
            public bool JobHook(Occupation c)
            {
                DrTravelStep("CareerManager.AssignJob", "enter");
                try { return JobOrig(c); }
                finally { try { if (c != null) DdNote(c.OwnerDescription); } catch (Exception e) { DrTravelFail("CareerManager.AssignJob", e); } DrTravelStep("CareerManager.AssignJob", "done"); }
            }
            public bool JobOrig(Occupation c) { return false; }
        }

        public class DdAssignSchoolHook
        {
            public bool SchoolHook(School s)
            {
                DrTravelStep("CareerManager.AssignSchool", "enter");
                try { return SchoolOrig(s); }
                finally { try { if (s != null) DdNote(s.OwnerDescription); } catch (Exception e) { DrTravelFail("CareerManager.AssignSchool", e); } DrTravelStep("CareerManager.AssignSchool", "done"); }
            }
            public bool SchoolOrig(School s) { return false; }
        }

        public class DdSetBossHook
        {
            public void BossHook(SimDescription boss) { BossOrig(boss); DdNote(DdOwner(this)); }
            public void BossOrig(SimDescription boss) { } // Career.SetBoss (FormerBoss = Boss)
        }

        public class DdSchoolSetHook
        {
            public void SetHook() { SetOrig(); DdNoteBoth(DdOwner(this)); }
            public void SetOrig() { } // School.SetCoworkersAndBoss (Coworkers.Add on both sides)
        }

        public class DdStudentHook
        {
            public void SetHook() { SetOrig(); DdNoteBoth(DdOwner(this)); }
            public void SetOrig() { } // AcademicCareer.SetUpStudent (Coworkers.Add on both sides)
        }

        public class DdOccImportHook
        {
            public bool ImportHook(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader)
            {
                DrTravelStep("Occupation.ImportContent", "enter");
                bool ok = ImportOrig(resKeyTable, objIdTable, reader);
                try { DdNoteHeld(DdOwner(this)); } // CareerManager assigns mJob / mSchool after this returns: the owner is stepped next run
                catch (Exception e) { DrTravelFail("Occupation.ImportContent", e); }
                DrTravelStep("Occupation.ImportContent", "done");
                return ok;
            }
            public bool ImportOrig(ResKeyTable resKeyTable, ObjectIdTable objIdTable, IPropertyStreamReader reader) { return false; } // Occupation.ImportContent (FormerBoss)
        }

        public class DdChessHook
        {
            public void FindHook() { FindOrig(); LogicSkill l = (object)this as LogicSkill; if (l != null) DdNote(l.SkillOwner); }
            public void FindOrig() { } // LogicSkill.FindSuitableChessRankOpponent
        }

        public static class DdPregStartHook
        {
            public static bool StartHook(Sim woman, SimDescription manDesc) { bool ok = StartOrig(woman, manDesc); if (woman != null) DdNote(woman.SimDescription); return ok; }
            public static bool StartOrig(Sim woman, SimDescription manDesc) { return false; } // Pregnancy.StartInternal(Sim, SimDescription)
        }

        public static class DdPregMergeHook
        {
            public static void MergeHook(Sim woman, Pregnancy incomingPreg)
            {
                DrTravelStep("Pregnancy.StartFromMerge", "enter");
                MergeOrig(woman, incomingPreg);
                try { if (woman != null) DdNote(woman.SimDescription); } catch (Exception e) { DrTravelFail("Pregnancy.StartFromMerge", e); }
                DrTravelStep("Pregnancy.StartFromMerge", "done");
            }
            public static void MergeOrig(Sim woman, Pregnancy incomingPreg) { } // Pregnancy.StartFromMerge
        }

        public class DdPregContinueHook
        {
            public bool ContinueHook(Sim mom, bool momJustCreated) { bool ok = ContinueOrig(mom, momJustCreated); if (mom != null) DdNote(mom.SimDescription); return ok; }
            public bool ContinueOrig(Sim mom, bool momJustCreated) { return false; } // Pregnancy.Continue
        }

        public class DdPetPregContinueHook
        {
            public bool ContinueHook(Sim mom, bool momJustCreated) { bool ok = ContinueOrig(mom, momJustCreated); if (mom != null) DdNote(mom.SimDescription); return ok; }
            public bool ContinueOrig(Sim mom, bool momJustCreated) { return false; } // PetPregnancy.Continue
        }
    }
}
