using System;
using Sims3.Gameplay.Core;
using System.Collections.Generic;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Autonomy;
using Sims3.Gameplay.Interfaces;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // venueintensity (tuning kVenueIntensity, default off; docs/661 §3.2). MetaAutonomyTuning.GetIntensity, the per-Sim
    // weight of every venue, park, pool, graveyard, rabbit hole, pond and garden in meta-autonomy (Lot.HowMuchItWantsSimToCome,
    // RabbitHole, WorldPondHelper, GardeningMetaObject, GoHome), reads
    //   float num = IsDowntownWorld() ? Intensity : Intensity_Downtown;
    // [verified IL IL_0072 call IsDowntownWorld, brfalse -> Intensity_Downtown, else Intensity]: the Venues XML's
    // Intensity_Downtown column is used in every world except a downtown one (Bridgeport), and the plain Intensity column
    // only in Bridgeport. A venue whose two columns differ is weighted with the other world's value.
    // Fix: for a non-exempt NPC, when the right column for this world is set (> 0) and differs from what EA reads
    // (LotGameplayCore.ShouldSwap), the two fields are swapped around EA's own GetIntensity body and swapped back in a
    // finally (the body does not yield; sim thread), so every other factor (traits, days, rich, celebrity, pets, the
    // active-Sim 2000) stays EA's. Household, selected and unknown-scope Sims (IsPlayerSim) get EA's value. Each venue
    // type's two columns are logged once a session while on ("venueintensity: <type> Intensity a, Intensity_Downtown b"),
    // so the first run shows which venues the swap changes (the Venues XML is not in game-copy).
    // Counters [4236] venue types logged, [4237] NPC calls swapped, [4238] exempt calls on a venue that would swap,
    // [4239] errors. Log capped at 40 lines a minute (the type dump).
    public static partial class Fixes
    {
        static readonly Dictionary<int, bool> sViLogged = new Dictionary<int, bool>();
        static int sViMinute = -1, sViLines;

        static void ApplyVenueIntensity()
        {
            Patch(typeof(MetaAutonomyTuning), "GetIntensity", new Type[] { typeof(IMetaObject), typeof(Sim) }, typeof(VenueIntensityHook), "IntensityHook", "IntensityOrig");
        }

        static void ViLog(string s)
        {
            int minute = System.Environment.TickCount / 60000;
            if (minute != sViMinute) { sViMinute = minute; sViLines = 0; }
            if (sViLines++ < 40) DiagLine("venueintensity: " + s);
        }

        public class VenueIntensityHook : MetaAutonomyTuning
        {
            VenueIntensityHook() : base(MetaAutonomyVenueType.Restaurant) { }  // never constructed: the class only lends `this`

            public float IntensityHook(IMetaObject metaObject, Sim sim)
            {
                if (sViBypass || !ShortcutOn("venueintensity") || sim == null) return IntensityOrig(metaObject, sim);  // sViBypass: VenueIntensityCheck's EA reference calls
                bool swap = false;
                int[] c = PatchCounters();
                try
                {
                    bool downtown = GameUtils.IsDowntownWorld();
                    int key = (int)Type;
                    if (!sViLogged.ContainsKey(key) && sViLogged.Count < 512)
                    {
                        sViLogged[key] = true;
                        c[4236]++;
                        ViLog(Type + " Intensity " + Intensity + ", Intensity_Downtown " + Intensity_Downtown + (downtown ? " (downtown world)" : "")
                            + (LotGameplayCore.ShouldSwap(downtown, Intensity, Intensity_Downtown) ? ": swapped for NPCs" : ""));
                    }
                    if (LotGameplayCore.ShouldSwap(downtown, Intensity, Intensity_Downtown))
                    {
                        if (IsPlayerSim(sim)) c[4238]++;
                        else swap = true;
                    }
                }
                catch (Exception) { c[4239]++; swap = false; }
                if (!swap) return IntensityOrig(metaObject, sim);
                float a = Intensity, b = Intensity_Downtown;
                Intensity = b; Intensity_Downtown = a;
                try
                {
                    float r = IntensityOrig(metaObject, sim);
                    c[4237]++;
                    return r;
                }
                finally { Intensity = a; Intensity_Downtown = b; }
            }
            public float IntensityOrig(IMetaObject metaObject, Sim sim) { return 0f; } // receives MetaAutonomyTuning.GetIntensity's original body
        }

        // Forced live check (repro venueintensity-check, agent reflect_call); returns a text with a "verdict:" line.
        // run: for one NPC, every venue type whose two intensity columns differ in a way LotGameplayCore.ShouldSwap fixes:
        // GetIntensity(null, npc) through the hook vs EA's body with the columns as loaded (ea) and swapped (right).
        // Off: hook == ea != right (BROKEN). On: hook == right (FIXED). No such type: N/A (the columns agree in this install).
        static bool sViBypass;
        public static object VenueIntensityCheck(string what)
        {
            int[] c = PatchCounters();
            string counters = "4236 types logged " + c[4236] + ", 4237 swapped " + c[4237] + ", 4238 exempt " + c[4238] + ", 4239 errors " + c[4239];
            if (what == "result") return counters;
            if (what != "run") return "venueintensity <run|result>";
            try
            {
                Sim npc = null;
                foreach (Sim s in LotManager.Actors)
                    if (s != null && !s.HasBeenDestroyed && s.SimDescription != null && s.SimDescription.IsHuman && !IsPlayerSim(s)) { npc = s; break; }
                if (npc == null) return "verdict: N/A (no NPC instantiated)\n" + counters;
                bool downtown = GameUtils.IsDowntownWorld();
                List<string> rows = new List<string>();
                int broken = 0, fixd = 0;
                foreach (MetaAutonomyVenueType t in Enum.GetValues(typeof(MetaAutonomyVenueType)))
                {
                    MetaAutonomyTuning tu = MetaAutonomyManager.GetTuning(t);
                    if (tu == null || !LotGameplayCore.ShouldSwap(downtown, tu.Intensity, tu.Intensity_Downtown)) continue;
                    float hook = tu.GetIntensity(null, npc), ea, right;
                    float a = tu.Intensity, b = tu.Intensity_Downtown;
                    sViBypass = true;
                    try
                    {
                        ea = tu.GetIntensity(null, npc);
                        tu.Intensity = b; tu.Intensity_Downtown = a;
                        right = tu.GetIntensity(null, npc);
                    }
                    finally { tu.Intensity = a; tu.Intensity_Downtown = b; sViBypass = false; }
                    if (ea == right) continue;  // other factors zeroed both (excluded trait, active Sim here)
                    if (hook == right) fixd++; else if (hook == ea) broken++;
                    if (rows.Count < 25) rows.Add(t + ": Intensity " + a + ", Intensity_Downtown " + b + " -> hook " + hook + ", ea " + ea + ", right " + right);
                }
                string head = (downtown ? "downtown world" : "not a downtown world") + ", NPC " + npc.FullName + "\n" + string.Join("\n", rows.ToArray());
                string verdict = broken + fixd == 0 ? "N/A (no venue type whose columns differ in this install)" : broken > 0 ? "BROKEN (" + broken + " venue types use the other world's intensity)" : "FIXED (" + fixd + " venue types)";
                return head + "\nswitch " + (ShortcutOn("venueintensity") ? "on" : "off") + "\nverdict: " + verdict + "\n" + counters;
            }
            catch (Exception ex) { sViBypass = false; return "verdict: N/A (error " + ex.GetType().Name + ": " + ex.Message + ")\n" + counters; }
        }
    }
}
