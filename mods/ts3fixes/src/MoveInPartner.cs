using System;
using System.Reflection;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.StoryProgression;

namespace Ts3Fixes
{
    // moveinpartner (tuning kMoveInPartner, on by default since 2026-11-15; docs/563 §3). EA bug fix; NPC households only: EA's action
    // already skips the active household, and MipExempt (IsPlayerDesc on both Sims; counter 1046) leaves any household, selected or unknown-scope Sim to EA.
    //
    // StoryProgression MoveInWithPartner [verified, decompiled]: NotLivingWithPartner accepts a couple when either way
    // works: (Sim can leave and the partner's household has room for it) OR (partner can leave and the Sim's household has
    // room). The constructor then picks the mover by "can leave" only: the Sim whenever CanRemoveSimFromHousehold(Sim),
    // without the room test. IsValid re-runs NotLivingWithPartner, which passes on the other branch, and Execute adds the
    // Sim to a household that has no room (Household.CanAddSimDescriptionToHousehold: 8 Sims, 6 pets, 10 in all), so
    // story progression grows NPC households past the cap (the cap every other path, the player's included, keeps).
    // Fix: before EA's Execute, when the partner's household has no room for the chosen Sim, the partner moves instead if
    // that is the valid way (partner can leave and the Sim's household has room); otherwise the move is skipped.
    // Teens (docs/563 §5): "Partner" is the going-steady LTR (EA socializing data: SetsPartner 1 for Partner, Fiancee,
    // Spouse; Prom "seal the deal" also calls SetPartner), and neither NotLivingWithPartner nor SelectRandomSimDescription
    // filters by age, so this action moves a teen out of the parents' home into the steady's family (or takes the steady
    // in). The player's own Ask to Move In needs a young adult. Fix: the move is skipped when either Sim is a teen or
    // younger.
    // Counters: [2510] mover swapped to the partner, [2511] moves skipped, [2512] errors, [2516] teen moves skipped.
    // Check: MoveInPartnerChecks.cs.
    public static partial class Fixes
    {
        static readonly Type sMipType = typeof(StoryAction).Assembly.GetType("Sims3.Gameplay.StoryProgression.MoveInWithPartner");
        static readonly FieldInfo sMipSim = sMipType == null ? null : sMipType.GetField("mSim", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        static readonly MethodInfo sMipCanRemove = sMipType == null ? null : sMipType.GetMethod("CanRemoveSimFromHousehold", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        static void ApplyMoveInPartner()
        {
            if (sMipSim == null || sMipCanRemove == null) { AppliedLog().Add("moveinpartner: MoveInWithPartner members not found, not patched"); return; }
            Patch(sMipType, "Execute", typeof(MipHook), "ExecuteHook", "ExecuteOrig");
        }

        // The decision (also used by the check): 0 = EA's move, 1 = move the partner instead, 2 = skip the move.
        internal static int MipAction(bool on, bool roomForSim, bool partnerCanMove)
        {
            if (!on || roomForSim) return 0;
            return partnerCanMove ? 1 : 2;
        }

        // Household rule (CLAUDE.md): a move-in that touches a household, selected or unknown-scope Sim (IsPlayerDesc) is left to EA.
        internal static bool MipExempt(bool simPlayer, bool partnerPlayer)
        {
            return simPlayer || partnerPlayer;
        }

        // Teens: skip the story move when either Sim is a teen or younger.
        internal static bool MipTeenSkip(bool on, bool teenOrBelow)
        {
            return on && teenOrBelow;
        }

        public class MipHook
        {
            public void ExecuteHook()
            {
                if (ShortcutOn("moveinpartner"))
                {
                    try
                    {
                        object a = this;
                        SimDescription sd = SimDescription.Find((ulong)sMipSim.GetValue(a));
                        SimDescription partner = sd == null ? null : sd.Partner;
                        if (partner != null && MipExempt(IsPlayerDesc(sd), IsPlayerDesc(partner))) PatchCounters()[1046]++;  // EA's move, untouched
                        else if (partner != null && MipTeenSkip(true, sd.TeenOrBelow || partner.TeenOrBelow)) { PatchCounters()[2516]++; return; }
                        else if (partner != null && partner.Household != null && sd.Household != null)
                        {
                            bool room = partner.Household.CanAddSimDescriptionToHousehold(sd);
                            bool other = !room && (bool)sMipCanRemove.Invoke(a, new object[] { partner }) && sd.Household.CanAddSimDescriptionToHousehold(partner);
                            int act = MipAction(true, room, other);
                            if (act == 1) { sMipSim.SetValue(a, partner.SimDescriptionId); PatchCounters()[2510]++; }
                            else if (act == 2) { PatchCounters()[2511]++; return; }
                        }
                    }
                    catch (Exception) { PatchCounters()[2512]++; }
                }
                ExecuteOrig();
            }
            public void ExecuteOrig() { } // receives MoveInWithPartner.Execute's original body
        }
    }
}
