using System;
using Sims3.Gameplay.CAS;
using Sims3.SimIFace;
using Sims3.SimIFace.CAS;

namespace Ts3Agent
{
    // thumb_outfits {sims?, perSim?}: repro trigger for the Sim thumbnail stall ([ThumbStall], thumbstall.cpp, issue #40).
    // Asks the native ThumbnailManager for portraits of townies' unworn outfits: the requests the SimService builds last
    // (thumbstall.cpp "Where the LOD waits": an outfit nobody wears has no world model, so its LOD job loses to every job
    // of a Sim nearer the camera). The same call as EA's pack-up portrait (MiniSimDescription.GenerateCrossWorldThumbnail:
    // outfit key, cache id = its instance, group 0, size 4), but invalidate (render again even when cached) and no wait,
    // so the command returns at once. Only Sims of households other than the active one (household Sims untouched; a Sim
    // with no household is skipped). Nothing about a Sim changes: the render lands in the thumbnail cache.
    public static partial class Handlers
    {
        static readonly OutfitCategories[] kThumbCats = {
            OutfitCategories.Everyday, OutfitCategories.Formalwear, OutfitCategories.Sleepwear, OutfitCategories.Swimwear,
            OutfitCategories.Athletic, OutfitCategories.Career, OutfitCategories.Outerwear };

        static object ThumbOutfits(Args a)
        {
            RequireLive();
            int maxSims = (int)a.Double("sims", 20), perSim = (int)a.Double("perSim", 4);
            Household active = Household.ActiveHousehold;
            int sims = 0, asked = 0, skipped = 0;
            foreach (Household h in Household.sHouseholdList)
            {
                if (sims >= maxSims) break;
                if (h == null || h == active || h.AllSimDescriptions == null) continue;
                foreach (SimDescription sd in h.AllSimDescriptions)
                {
                    if (sims >= maxSims) break;
                    if (sd == null || !sd.IsHuman || sd.Household != h) { skipped++; continue; }
                    OutfitCategories worn = sd.CreatedSim != null ? sd.CreatedSim.CurrentOutfitCategory : OutfitCategories.None;
                    int n = 0;
                    foreach (OutfitCategories cat in kThumbCats)
                    {
                        if (n >= perSim) break;
                        if (cat == worn) continue;
                        SimOutfit o = null;
                        try { if (sd.GetOutfitCount(cat) > 0) o = sd.GetOutfit(cat, 0); } catch (Exception) { }
                        if (o == null || o.Key.InstanceId == 0) continue;
                        ThumbnailManager.GenerateHouseholdSimThumbnail(o.Key, o.Key.InstanceId, 0u, (ThumbnailSizeMask)4,
                            (ThumbnailTechnique)0, true, false, sd.AgeGenderSpecies);
                        n++;
                    }
                    asked += n;
                    if (n > 0) sims++;
                }
            }
            System.Collections.Generic.Dictionary<string, object> r = Util.Obj();
            r["sims"] = sims;
            r["asked"] = asked;
            r["skipped"] = skipped;
            return r;
        }
    }
}
