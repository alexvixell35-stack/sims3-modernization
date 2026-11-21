using System;
using System.Collections.Generic;
using System.Text;
using Sims3.Gameplay;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Situations;
using Sims3.Gameplay.Utilities;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // Harness check for PartyFixes.cs (docs/554; test only, reflect_call). PartyFixCheck(what):
    //   "tuning" (read-only): EA's party mood thresholds as tuned, EP9 / University world, and the verdict for the per-guest
    //     tier order: BUG LIVE when Amazing/Killer/Raging lie above Great (then GetPartyOutcomeForSim can never return them),
    //     N/A when not.
    //   "outcome" (read-only): every running HouseParty (and the ApartmentLeaveCheck "throw" party): EA's party outcome as
    //     the hooks answer now (off = EA, on = fixed), whether partyoutcome's lame rule applies (score below the base lame
    //     threshold with a Neutral result), and per guest the outcome as answered now vs the fixed tier order.
    //   "force" (changes the world briefly, test saves only): deterministic version of both fixes, no waiting for natural
    //     play. A HouseParty (no invitations, host = a household member at home, one sim-day ahead) is made, then:
    //     outcome  EA's own counters are set to one leaver with a score 5 below the base lame threshold
    //              (mPartyMood / mNumberOfGuests) and GetPartyOutcome is asked: off kNeutralParty = BROKEN (EP9, not a
    //              University world), on kBadParty with 2400 moving = FIXED; kBadParty with no counter move = NOT TRIGGERED
    //              (the loaded game code is already fixed, e.g. simler90's core mod; record N/A);
    //     guest    the tier thresholds are set around the host's current mood for one call (Great and Amazing below it,
    //              Killer and Raging far above) and GetPartyOutcomeForSim(host) is asked, then the tuning is restored:
    //              off kGoodParty = BROKEN (EA's Good-first order), on kAmazingParty with 2401 moving = FIXED, without a
    //              counter move NOT TRIGGERED as above;
    //     motive   (on a script task: RemoveGuest can yield) one adult NPC becomes a guest, the party enters EA's Happening
    //              state (party motives created), the NPC leaves early through EA's RemoveGuest and its enjoy-party motive
    //              is read: off still there = BROKEN, on gone = FIXED. The party is exited after. Read with "forceresult".
    //   "motives": every Sim who left a running party (SimsWhoLeftParty) and still carries an enjoy-party motive:
    //     off -> they are listed (BROKEN when any), on -> none for guests who left after the switch was on (FIXED).
    public static partial class Fixes
    {
        public static object PartyFixCheck(string what)
        {
            string mode = (what ?? "").Trim();
            if (mode.Length == 0) mode = "tuning";
            if (!GameStates.IsInWorld()) return "party fix check: not in a world";
            try
            {
                if (mode == "tuning") return PfcTuning();
                if (mode == "outcome") return PfcOutcome();
                if (mode == "motives") return PfcMotives();
                if (mode == "force") return PfcForce();
                if (mode == "forceresult") return sPfcForceResult ?? "forceresult: none yet (run force; the motive part finishes on a script task a moment later)";
                return "party fix check: tuning | outcome | motives | force | forceresult";
            }
            catch (Exception e) { return "party fix check: " + e.GetType().Name + ": " + e.Message; }
        }

        static string PfcSwitches()
        {
            int[] c = PatchCounters();
            return "partyoutcome " + (ShortcutOn("partyoutcome") ? "on" : "off")
                + ", partymotive " + (ShortcutOn("partymotive") ? "on" : "off") + "; counters 2400-2404: " + c[2400] + " " + c[2401] + " " + c[2402] + " " + c[2403] + " " + c[2404];
        }

        static object PfcTuning()
        {
            float great = PfTuning("kThrewGreatPartyBuffMoodRequirement"), lame = PfTuning("kThrewLamePartyBuffMoodRequirement");
            float amazing = PfTuning("kThrewAmazingPartyBuffMoodRequirement"), killer = PfTuning("kThrewKillerPartyBuffMoodRequirement");
            float raging = PfTuning("kThrewRagingPartyBuffMoodRequirement"), lame9 = PfTuning("kThrewLamePartyBuffMoodRequirementEP9");
            bool ep9 = GameUtils.IsInstalled(ProductVersion.EP9), uni = GameUtils.IsUniversityWorld();
            bool order = ep9 && (amazing > great || killer > great || raging > great);
            return "tuning: lame " + lame + " lameEP9 " + lame9 + " great " + great + " amazing " + amazing + " killer " + killer + " raging " + raging
                + "; EP9 " + ep9 + " University world " + uni
                + "\nguest tier order: " + (order ? "BUG LIVE (a tier above Great: EA's per-guest order never returns it)" : "N/A (no EP9 tier above Great)")
                + "\nlame outside University worlds: " + (ep9 && !uni ? "BUG LIVE when a party scores below lameEP9 (EA leaves it Neutral)" : "N/A here")
                + "\n" + PfcSwitches();
        }

        static List<HouseParty> PfcParties()
        {
            List<HouseParty> list = new List<HouseParty>();
            foreach (Situation x in Situation.sAllSituations) { HouseParty h = x as HouseParty; if (h != null) list.Add(h); }
            return list;
        }

        static object PfcOutcome()
        {
            StringBuilder sb = new StringBuilder();
            List<HouseParty> parties = PfcParties();
            if (parties.Count == 0) return "outcome: N/A (no running HouseParty; ApartmentLeaveCheck 'throw 6 now' starts one)\n" + PfcSwitches();
            foreach (HouseParty p in parties)
            {
                Party.PartyOutcomes now = p.GetPartyOutcome();
                sb.Append("party ").Append(p.GetType().Name).Append(" on ").Append(p.Lot != null ? p.Lot.Name : "?").Append(" host ").Append(p.Host != null ? p.Host.FullName : "?")
                  .Append(": outcome now ").Append(now).Append(PfLameMissed(p, now) ? " (EA lame miss: fix makes it kBadParty)" : "").Append('\n');
                List<Sim> all = new List<Sim>(p.Guests);
                foreach (Sim s in p.SimsWhoLeftParty) if (!all.Contains(s)) all.Add(s);
                foreach (Sim s in all)
                {
                    if (s == null || s.HasBeenDestroyed) continue;
                    Party.PartyOutcomes asked = p.GetPartyOutcomeForSim(s), fixedOrder = PfGuestOutcome(p, s);
                    sb.Append("  ").Append(s.FullName).Append(" mood ").Append(s.MoodManager.MoodValue).Append(": answered ").Append(asked).Append(", tier order ").Append(fixedOrder)
                      .Append(asked == fixedOrder ? "" : " DIFFERENT (EA order)").Append('\n');
                }
            }
            return sb.Append(PfcSwitches()).ToString();
        }

        static string sPfcForceResult;

        static void PfcSetTuning(string name, float v)
        {
            System.Reflection.FieldInfo f = typeof(HouseParty).GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (f != null) f.SetValue(null, v);
        }

        // Force verdict: EA's wrong answer = BROKEN; the fixed answer = FIXED when the hook made it (counter moved), and
        // NOT TRIGGERED when the loaded game code already answers correctly (a core mod that replaces Sims3GameplaySystems,
        // e.g. simler90's, ships these fixes: AGENTS.md records that as N/A, not FAIL).
        static string PfcVerdict(bool on, Party.PartyOutcomes got, Party.PartyOutcomes ea, Party.PartyOutcomes fixedTier, int moved)
        {
            if (got == ea) return on ? "BROKEN (switch on, hook did not change it)" : "BROKEN";
            if (got != fixedTier) return "OTHER";
            if (moved > 0) return on ? "FIXED" : "OTHER (hook changed it with the switch off)";
            return "NOT TRIGGERED (the loaded HouseParty already answers correctly" + PfcCoreMod() + "; record N/A)";
        }

        static string PfcCoreMod()
        {
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name;
                if (n != null && n.StartsWith("simler90", StringComparison.OrdinalIgnoreCase)) return ": " + n + " loaded, its core mod replaces Sims3GameplaySystems";
            }
            return ": a core mod may replace Sims3GameplaySystems";
        }

        static object PfcForce()
        {
            Sim sel = Sims3.Gameplay.Core.PlumbBob.SelectedActor;
            if (sel == null || sel.Household == null || sel.LotHome == null) return "force: N/A (no selected Sim with a home lot)";
            Sims3.Gameplay.Core.Lot lot = sel.LotHome;
            Sim host = null;
            foreach (Sim m in sel.Household.Sims) if (m != null && !m.HasBeenDestroyed && m.SimDescription.TeenOrAbove && (host == null || m == sel)) host = m;
            if (host == null) return "force: N/A (no teen-or-older household member)";
            bool ep9 = GameUtils.IsInstalled(ProductVersion.EP9), uni = GameUtils.IsUniversityWorld();
            bool legendary = host.HasTrait(Sims3.Gameplay.ActorSystems.TraitNames.LegendaryHost);
            DateAndTime start = SimClock.CurrentTime() + new DateAndTime(SimClock.ConvertToTicks(24f, TimeUnit.Hours));
            HouseParty p = new HouseParty(lot, host, new List<SimDescription>(), Sims3.SimIFace.CAS.OutfitCategories.Everyday, start, false);
            StringBuilder sb = new StringBuilder();
            const System.Reflection.BindingFlags inst = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            int[] c = PatchCounters();
            bool on = ShortcutOn("partyoutcome");
            // outcome: one leaver whose score is 5 below the base lame threshold. EA (EP9, not a University world) leaves it
            // Neutral; the base lame rule (and the fix) gives Bad.
            float lame = PfTuning("kThrewLamePartyBuffMoodRequirement"), lame9 = PfTuning("kThrewLamePartyBuffMoodRequirementEP9");
            typeof(HouseParty).GetField("mPartyMood", inst).SetValue(p, lame - 5f);
            typeof(HouseParty).GetField("mNumberOfGuests", inst).SetValue(p, 1);
            int c0 = c[2400];
            Party.PartyOutcomes o = p.GetPartyOutcome();
            string ov = legendary ? "N/A (legendary host: always good)" : !ep9 || uni ? "N/A (" + (!ep9 ? "no EP9" : "University world") + ": EA's base lame test runs)"
                : PfcVerdict(on, o, Party.PartyOutcomes.kNeutralParty, Party.PartyOutcomes.kBadParty, c[2400] - c0);
            sb.Append("force outcome: score ").Append(lame - 5f).Append(" (lame ").Append(lame).Append(", lameEP9 ").Append(lame9).Append("), EA gives kNeutralParty, fixed kBadParty -> ")
              .Append(o).Append(", 2400 +").Append(c[2400] - c0).Append(": ").Append(ov).Append('\n');
            typeof(HouseParty).GetField("mPartyMood", inst).SetValue(p, 0f);
            typeof(HouseParty).GetField("mNumberOfGuests", inst).SetValue(p, 0);
            // guest tier order: Great and Amazing below the host's mood, Killer and Raging far above. EA's Good-first order
            // gives Good; GetPartyOutcome's order (the fix) gives Amazing.
            string[] names = { "kThrewGreatPartyBuffMoodRequirement", "kThrewAmazingPartyBuffMoodRequirement", "kThrewKillerPartyBuffMoodRequirement", "kThrewRagingPartyBuffMoodRequirement" };
            float[] keep = new float[names.Length];
            for (int i = 0; i < names.Length; i++) keep[i] = PfTuning(names[i]);
            float m0 = host.MoodManager.MoodValue;
            int g0 = c[2401];
            Party.PartyOutcomes g;
            try
            {
                PfcSetTuning(names[0], m0 - 30f); PfcSetTuning(names[1], m0 - 20f); PfcSetTuning(names[2], m0 + 1000f); PfcSetTuning(names[3], m0 + 2000f);
                g = p.GetPartyOutcomeForSim(host);
            }
            finally { for (int i = 0; i < names.Length; i++) PfcSetTuning(names[i], keep[i]); }
            string gv = !ep9 ? "N/A (no EP9: no tier above Great)" : legendary ? "N/A (legendary host)"
                : PfcVerdict(on, g, Party.PartyOutcomes.kGoodParty, Party.PartyOutcomes.kAmazingParty, c[2401] - g0);
            sb.Append("force guest: mood ").Append(m0).Append(" with Great ").Append(m0 - 30f).Append(" / Amazing ").Append(m0 - 20f).Append(", EA gives kGoodParty, fixed kAmazingParty -> ")
              .Append(g).Append(", 2401 +").Append(c[2401] - g0).Append(": ").Append(gv).Append(" (tuning restored)\n");
            // motive (script task)
            Sim npc = AlPickNpc(lot);
            if (npc == null) { p.Exit(); return sb.Append("force motive: N/A (no adult NPC in the world)\n").Append(PfcSwitches()).ToString(); }
            sPfcForceResult = null;
            Simulator.AddObject(new LoopTask(delegate
            {
                string r;
                try
                {
                    AlMakeGuest(p, npc);
                    p.SetState(new HouseParty.Happening(p));
                    CommodityKind k = p.BeAtHostedSituationMotive(npc);
                    bool had = npc.Motives.HasMotive(k);
                    p.RemoveGuest(npc, true);
                    bool still = npc.Motives.HasMotive(k);
                    r = "force motive: " + npc.FullName + " guest, Happening gave " + k + "=" + had + ", left early (EA RemoveGuest), still has it=" + still + ": "
                        + (!had ? "N/A (no motive created)" : still ? "BROKEN" : "FIXED");
                    if (still) npc.Motives.RemoveMotive(k);  // test cleanup
                }
                catch (Exception e) { r = "force motive: error " + e.GetType().Name + ": " + e.Message; }
                try { p.Exit(); r += "; party exited"; } catch (Exception) { r += "; exit failed"; }
                sPfcForceResult = r + "\n" + PfcSwitches();
            }));
            return sb.Append("force motive: queued on a script task (read with forceresult)\n").Append(PfcSwitches()).ToString();
        }

        static object PfcMotives()
        {
            StringBuilder sb = new StringBuilder();
            int bad = 0, seen = 0;
            foreach (HouseParty p in PfcParties())
                foreach (Sim s in p.SimsWhoLeftParty)
                {
                    if (s == null || s.HasBeenDestroyed || s.Motives == null) continue;
                    seen++;
                    CommodityKind k = p.BeAtHostedSituationMotive(s);
                    if (!s.Motives.HasMotive(k)) continue;
                    bad++;
                    sb.Append("  ").Append(s.FullName).Append(" left ").Append(p.GetType().Name).Append(" and still has ").Append(k).Append('\n');
                }
            sb.Append("motives: ").Append(seen).Append(" guests left running parties, ").Append(bad).Append(" still carry the party motive: ")
              .Append(seen == 0 ? "N/A (no guest has left a running party yet)" : bad > 0 ? "BROKEN" : "FIXED/OK").Append('\n');
            return sb.Append(PfcSwitches()).ToString();
        }
    }
}
