using System;
using System.Collections.Generic;
using Sims3.Gameplay;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Objects;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;

namespace Ts3Agent
{
    // inv_stuff {per_sim?: 8, skip?: (restart at this catalogue position), townies?: 6, household?: true, shuffle?: true, select?: true} (docs/191 leak hunts):
    // STRESS, not a player flow. Puts per_sim EA catalogue objects (distinct products from #skip, catalogue order) straight
    // into the inventories of the active household's Sims and of up to `townies` instantiated NPC townies, then
    // (shuffle) moves every added object one Sim along the ring of chosen Sims, and (select) selects each household Sim
    // in turn so the live-mode inventory panel rebuilds its InventoryItemWin rows. Every added object is tracked for
    // remove_objects, which takes them out of the inventory and destroys them: the run leaves inventories as they were.
    // Products the inventory refuses (TryToAdd false) are skipped for the session.
    public static partial class Handlers
    {
        static readonly Dictionary<ulong, bool> sIsRefused = new Dictionary<ulong, bool>();
        static int sIsCursor;  // position in the catalogue: calls in a row take the next products, wrapping at the end

        static void RegisterInventoryStress()
        {
            Dispatcher.Register("inv_stuff", InvStuff);
        }

        static object InvStuff(Args a)
        {
            RequireLive();
            int per = (int)a.Long("per_sim", 8), skip = (int)a.Long("skip", 0), maxTownies = (int)a.Long("townies", 6);
            if (per < 0 || per > 40) throw new McpException("per_sim is 0..40 (0: only the select step)");
            List<Sim> sims = new List<Sim>();
            Household hh = Household.ActiveHousehold;
            if (a.Bool("household", true) && hh != null)
                foreach (Sim s in hh.AllActors) if (s != null && !s.HasBeenDestroyed && s.Inventory != null && s.SimDescription.IsHuman) sims.Add(s);
            int tw = 0;
            foreach (Sim s in Sims3.Gameplay.Queries.GetObjects<Sim>())
            {
                if (tw >= maxTownies) break;
                SimDescription d = s == null ? null : s.SimDescription;
                if (d == null || s.HasBeenDestroyed || s.Inventory == null || s.IsSelectable || d.Household == hh || d.Household == null
                    || d.AssignedRole != null || d.CreatedByService != null || !d.IsHuman || d.ChildOrBelow) continue;
                sims.Add(s); tw++;
            }
            if (sims.Count == 0) throw new McpException("no Sims with an inventory");
            List<BuildBuyProduct> prods = new List<BuildBuyProduct>();
            List<BuildBuyProduct> cat = SortedCatalog();
            if (a.Has("skip")) sIsCursor = skip;
            for (int n = 0; n < cat.Count && prods.Count < per * sims.Count * 3; n++)
            {
                BuildBuyProduct p = cat[(sIsCursor + n) % cat.Count];
                if (Placeable(p) && ContentOrigin(p.ProductResourceKey) == "ea" && !sIsRefused.ContainsKey(p.ProductResourceKey.InstanceId)) prods.Add(p);
            }
            List<ulong> created = CreatedObjects();
            Dictionary<Sim, List<GameObject>> added = new Dictionary<Sim, List<GameObject>>();
            int ok = 0, refused = 0, pi = 0;
            foreach (Sim s in sims)
            {
                List<GameObject> mine = new List<GameObject>();
                added[s] = mine;
                while (mine.Count < per && pi < prods.Count)
                {
                    BuildBuyProduct p = prods[pi++];
                    GameObject g = null;
                    try { g = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject; } catch (Exception) { }
                    if (g == null || !s.Inventory.TryToAdd(g))
                    {
                        if (g != null) g.Destroy();
                        sIsRefused[p.ProductResourceKey.InstanceId] = true;
                        refused++;
                        continue;
                    }
                    created.Add(g.ObjectId.Value);
                    mine.Add(g);
                    ok++;
                }
            }
            int moved = 0, moveFail = 0;
            if (a.Bool("shuffle", true) && sims.Count > 1)
                for (int i = 0; i < sims.Count; i++)
                {
                    Sim to = sims[(i + 1) % sims.Count];
                    foreach (GameObject g in added[sims[i]])
                    {
                        try
                        {
                            if (sims[i].Inventory.TryToRemove(g) && to.Inventory.TryToAdd(g)) moved++;
                            else moveFail++;
                        }
                        catch (Exception) { moveFail++; }
                    }
                }
            int selected = 0;
            if (a.Bool("select", true) && hh != null)
            {
                Sim was = PlumbBob.SelectedActor;
                foreach (Sim s in hh.AllActors)
                    if (s != null && s.IsSelectable && s != was) { try { if (PlumbBob.SelectActor(s)) selected++; } catch (Exception) { } }
                if (was != null) { try { PlumbBob.SelectActor(was); } catch (Exception) { } }
            }
            Dictionary<string, object> r = Util.Obj();
            r["sims"] = sims.Count;
            r["townies"] = tw;
            r["added"] = ok;
            r["refused"] = refused;
            r["refused_session"] = sIsRefused.Count;
            r["moved"] = moved;
            r["move_failed"] = moveFail;
            r["selected"] = selected;
            // advance past the products used or refused (positions in the whole catalogue)
            if (pi > 0)
            {
                ulong last = prods[pi - 1].ProductResourceKey.InstanceId;
                for (int n = 0; n < cat.Count; n++)
                    if (cat[(sIsCursor + n) % cat.Count].ProductResourceKey.InstanceId == last) { sIsCursor = (sIsCursor + n + 1) % cat.Count; break; }
            }
            r["cursor"] = sIsCursor;
            r["catalogue"] = cat.Count;
            r["tracked"] = created.Count;
            return r;
        }
    }
}
