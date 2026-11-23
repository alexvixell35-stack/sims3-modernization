using System;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace.CAS;

namespace Ts3Fixes
{
    // casoccultlog (tuning kCasOccultLog, default off; docs/658 §6.4). Two CAS methods swallow every exception
    // [verified, decompiled + ast-grep `try { $$$ } catch ($T) { }`]:
    // - CASLogic.SetCurrentOccultTypeSynchronous wraps HandleOccultChangeSynchronous (SetupSupernaturalData,
    //   PreviewSim.HandleOccultChange, the OccultTypeSelected event) and has already moved mLastOccultType;
    // - SetWerewolfAge.AgeChangeWerewolfOutfit (an age change of a werewolf in CAS) wraps the whole rebuild of the
    //   transformed outfit, so a throw part way leaves the Supernatural outfit unsaved.
    // The throw itself is not identified statically (no callee is provably null on these paths). This switch makes it
    // visible without changing what EA does: HandleOccultChangeSynchronous, StoreWerewolfOutfitToSupernaturalData and
    // SynchronizeSimAndBuilderHairColorToOutfit get a log-and-rethrow wrapper (EA's own catch still swallows), and
    // AgeChangeWerewolfOutfit logs when it returned without reaching StoreWerewolfOutfitToSupernaturalData (a throw in
    // its OutfitUtils steps). Lines "casoccultlog: ..." with the exception type, message and first stack frames, at most
    // 10 a minute and 60 per game. Counters [4210] occult change exceptions, [4211] werewolf age rebuilds that threw,
    // [4212] errors of the wrapper itself.
    public static partial class Fixes
    {
        static readonly LineCapCore sColCap = new LineCapCore(10, 60000, 60);
        // plain statics (the game's Mono has no thread statics): CAS operation steps run one at a time on the CAS task
        static int sColWwDepth;
        static bool sColWwStored, sColWwLogged;

        static void ColLog(string s)
        {
            if (sColCap.Take(System.Environment.TickCount)) DiagLine("casoccultlog: " + s);
        }

        static string ColWhat(Exception e)
        {
            string st = e.StackTrace ?? "";
            string[] lines = st.Split('\n');
            string top = "";
            for (int i = 0; i < lines.Length && i < 3; i++) top += (i > 0 ? " | " : "") + lines[i].Trim();
            return e.GetType().Name + ": " + e.Message + " @ " + top;
        }

        static void ApplyCasOccultLog()
        {
            Patch(typeof(CASLogic), "HandleOccultChangeSynchronous", new Type[] { typeof(Sims3.UI.Hud.OccultTypes), typeof(bool) }, typeof(CasOccHook), "HandleHook", "HandleOrig");
            Patch(typeof(CASLogic), "StoreWerewolfOutfitToSupernaturalData", new Type[] { typeof(SimBuilder) }, typeof(CasOccHook), "StoreHook", "StoreOrig");
            Patch(typeof(CASLogic), "SynchronizeSimAndBuilderHairColorToOutfit", new Type[] { typeof(SimOutfit) }, typeof(CasOccHook), "SyncHook", "SyncOrig");
            Type ww = typeof(CASLogic).GetNestedType("SetWerewolfAge", BindingFlags.NonPublic | BindingFlags.Public);
            if (ww == null) { AppliedLog().Add("casoccultlog: CASLogic.SetWerewolfAge not found"); return; }
            Patch(ww, "AgeChangeWerewolfOutfit", new Type[] { typeof(SimOutfit) }, typeof(CasWwHook), "AgeHook", "AgeOrig");
        }

        public abstract class CasOccHook : CASLogic
        {
            public void HandleHook(Sims3.UI.Hud.OccultTypes occultType, bool isInHousehold)
            {
                if (!ShortcutOn("casoccultlog")) { HandleOrig(occultType, isInHousehold); return; }
                bool noPreview = false;
                try { noPreview = PreviewSim == null; } catch (Exception) { PatchCounters()[4212]++; }
                try { HandleOrig(occultType, isInHousehold); }
                catch (Exception e)
                {
                    PatchCounters()[4210]++;
                    ColLog("SetCurrentOccultTypeSynchronous swallowed " + ColWhat(e) + " (to " + occultType + ", household " + isInHousehold
                        + (noPreview ? ", PreviewSim null" : "") + ")");
                    throw;
                }
            }
            public void HandleOrig(Sims3.UI.Hud.OccultTypes occultType, bool isInHousehold) { } // receives HandleOccultChangeSynchronous

            public void StoreHook(SimBuilder builder)
            {
                if (sColWwDepth == 0 || !ShortcutOn("casoccultlog")) { StoreOrig(builder); return; }
                sColWwStored = true;
                try { StoreOrig(builder); }
                catch (Exception e) { sColWwLogged = true; PatchCounters()[4211]++; ColLog("AgeChangeWerewolfOutfit swallowed (saving the outfit) " + ColWhat(e)); throw; }
            }
            public void StoreOrig(SimBuilder builder) { } // receives StoreWerewolfOutfitToSupernaturalData

            public void SyncHook(SimOutfit outfit)
            {
                if (sColWwDepth == 0 || !ShortcutOn("casoccultlog")) { SyncOrig(outfit); return; }
                try { SyncOrig(outfit); }
                catch (Exception e)
                {
                    sColWwLogged = true; PatchCounters()[4211]++;
                    ColLog("AgeChangeWerewolfOutfit swallowed (hair colours, current outfit " + (outfit == null ? "null" : "set") + ") " + ColWhat(e));
                    throw;
                }
            }
            public void SyncOrig(SimOutfit outfit) { } // receives SynchronizeSimAndBuilderHairColorToOutfit
        }

        public class CasWwHook
        {
            public void AgeHook(SimOutfit sourceOutfit)
            {
                if (!ShortcutOn("casoccultlog")) { AgeOrig(sourceOutfit); return; }
                sColWwDepth++; sColWwStored = false; sColWwLogged = false;
                try { AgeOrig(sourceOutfit); }
                finally
                {
                    sColWwDepth--;
                    if (CasOccultLogCore.WwThrew(sColWwStored, sColWwLogged))
                    {
                        PatchCounters()[4211]++;
                        ColLog("AgeChangeWerewolfOutfit threw before saving the werewolf outfit (an OutfitUtils step; source outfit "
                            + (sourceOutfit == null ? "null" : "set") + "): the Supernatural outfit keeps the old age");
                    }
                }
            }
            public void AgeOrig(SimOutfit sourceOutfit) { } // receives SetWerewolfAge.AgeChangeWerewolfOutfit
        }
    }
}
