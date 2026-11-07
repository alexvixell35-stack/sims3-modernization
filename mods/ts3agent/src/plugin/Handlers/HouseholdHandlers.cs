using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Interfaces;
using Sims3.Gameplay.Objects.Appliances;
using Sims3.Gameplay.Objects.Beds;
using Sims3.Gameplay.Socializing;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.SimIFace.CAS;
using Sims3.UI.CAS;
using Sims3.UI.Controller;

namespace Ts3Agent
{
    // Story households (docs/181): a household made the way a player makes one in Create A Sim and moves in through Edit Town,
    // with the game's own calls. Live mode only (our stand-in for Edit Town, which pauses the clock; the whole build is one
    // agent call on the sim thread). The generator (tools/harness/story-households.ts) picks valid CAS traits, the lifetime wish
    // and favourites; this handler checks them again with the game's own rules before anything is made.
    public static partial class Handlers
    {
        static void RegisterHouseholds()
        {
            Dispatcher.Register("make_household", MakeHousehold);
        }

        // make_household {name, sims: [{key, first, last, age, gender, traits: [TraitNames], wish?: primitive id (YA+),
        //   food?, music?, color?, zodiac?, voice?: A|B|C}], relations?: [{a, b, type: spouse|partner|parent|sibling|roommate}],
        //   lot?: lot id (else the most expensive vacant residential lot the funds buy that CanSupportPopulation), dry?: true,
        //   funds?: simoleons (the familyfunds cheat's effect: Household.SetFamilyFunds after creation, before the lot pick;
        //   realism cheat, docs/181 §3.1), empty_lot?: true (a vacant empty lot, nothing to furnish: a player who builds)}
        // CAS steps: SimUtils.SimCreationSpec.Instantiate (Genetics.MakeDescendant for a child of a man and a woman), the CAS
        // trait slots (TraitManager.RemoveAllElements + AddElement, age / visible / conflict / count checked like CAS), the CAS
        // favourites, zodiac and voice setters, SimDescription.LifetimeWish (DreamsAndPromisesManager.GetMajorDream must know it),
        // Genealogy.AddChild / AddSibling / Marry and the CAS relationship step (the CASLogic.LTRForceChangeState sequence), CAS
        // starting funds (CASLogic.GetHouseholdStartingFunds); move-in: BinCommon.PayForLot (furnished) + BinCommon.CreateAndMoveIn.
        // Caps (R-HH-1): 2..8 humans, no pets (pets come by adoption in play), no Baby (CAS makes Toddler and older).
        static object MakeHousehold(Args a)
        {
            RequireLive();
            // Edit Town's own refusal (move, reaper, social worker, age-up, fire, burglar: R-MODE-5)
            Household cur = Household.ActiveHousehold;
            string why = null;
            if (cur != null && cur.LotHome != null && !InWorldSubState.IsEditTownValid(cur.LotHome, ref why))
                throw new McpException("not available: Edit Town refused (" + (why ?? "busy") + ")");
            List<object> sims = a.List("sims");
            if (sims == null || sims.Count < 2 || sims.Count > 8) throw new McpException("sims: 2..8 humans (R-HH-1; pets join by adoption in play)");
            bool dry = a.Bool("dry", false);
            int adults = 0;
            List<Dictionary<string, object>> specs = new List<Dictionary<string, object>>();
            foreach (object o in sims)
            {
                Dictionary<string, object> s = o as Dictionary<string, object>;
                if (s == null) throw new McpException("sims: objects expected");
                CASAgeGenderFlags age = ParseFlag(Get(s, "age"));
                if (age == CASAgeGenderFlags.Baby) throw new McpException(Get(s, "first") + ": CAS makes Toddler and older; a Baby comes by birth or adoption");
                if ((age & (CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder)) != 0) adults++;
                specs.Add(s);
            }
            if (adults == 0) throw new McpException("a CAS household needs a Young Adult or older");

            // the traits and the wish are checked against the game's tables before any Sim exists
            List<string> problems = new List<string>();
            foreach (Dictionary<string, object> s in specs)
            {
                CASAgeGenderFlags age = ParseFlag(Get(s, "age"));
                CASAGSAvailabilityFlags ags = CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags(age | CASAgeGenderFlags.Human);
                List<TraitNames> names = TraitList(s);
                int slots = SlotsFor(age);
                if (names.Count > slots) problems.Add(Get(s, "first") + ": " + names.Count + " traits, " + age + " has " + slots);
                for (int i = 0; i < names.Count; i++)
                {
                    Trait t = TraitManager.GetTraitFromDictionary(names[i]);
                    if (t == null || !t.IsVisible) { problems.Add(Get(s, "first") + ": " + names[i] + " is not a CAS trait here"); continue; }
                    if (!t.TraitValidForAgeSpecies(ags)) problems.Add(Get(s, "first") + ": " + names[i] + " not for " + age);
                    for (int j = 0; j < i; j++) if (TraitManager.DoTraitsConflict(names[j], names[i])) problems.Add(Get(s, "first") + ": " + names[j] + " conflicts with " + names[i]);
                }
                if (s.ContainsKey("wish") && s["wish"] != null)
                {
                    if ((age & (CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder)) == 0) problems.Add(Get(s, "first") + ": lifetime wish only for YA+");
                    else if (DreamsAndPromisesManager.GetMajorDream(Convert.ToUInt32(s["wish"])) == null) problems.Add(Get(s, "first") + ": lifetime wish " + s["wish"] + " unknown (expansion?)");
                }
            }
            if (problems.Count > 0) throw new McpException(string.Join("; ", problems.ToArray()));

            Household h = Household.Create();
            Dictionary<string, SimDescription> byKey = new Dictionary<string, SimDescription>();
            Dictionary<string, object> r = Util.Obj();
            try
            {
                h.SetName(a.Str("name", "Story"));
                List<object> rel = a.List("relations") ?? new List<object>();
                foreach (Dictionary<string, object> s in specs)
                {
                    CASAgeGenderFlags age = ParseFlag(Get(s, "age"));
                    SimUtils.SimCreationSpec spec = new SimUtils.SimCreationSpec();
                    spec.Age = age;
                    spec.Gender = ParseFlag(Get(s, "gender"));
                    spec.GivenName = Get(s, "first");
                    spec.FamilyName = Get(s, "last");
                    spec.Normalize();
                    // a child of a man and a woman already made gets the game's genetics; everyone else a random CAS Sim
                    SimDescription dad = null, mom = null;
                    foreach (object o in rel)
                    {
                        Dictionary<string, object> x = o as Dictionary<string, object>;
                        SimDescription p;
                        if (x == null || Get(x, "type") != "parent" || Get(x, "b") != Get(s, "key") || !byKey.TryGetValue(Get(x, "a"), out p)) continue;
                        if (p.IsMale && dad == null) dad = p; else if (p.IsFemale && mom == null) mom = p;
                    }
                    SimDescription sd = dad != null && mom != null ? spec.Instantiate(dad, mom, false) : spec.Instantiate();
                    sd.FirstName = spec.GivenName;
                    sd.LastName = spec.FamilyName;
                    sd.TraitManager.RemoveAllElements();
                    foreach (TraitNames n in TraitList(s)) if (!sd.TraitManager.AddElement(n)) throw new McpException(sd.FullName + ": the game refused " + n);
                    if (s.ContainsKey("wish") && s["wish"] != null) sd.LifetimeWish = Convert.ToUInt32(s["wish"]);
                    if (Has(s, "food")) sd.FavoriteFood = (FavoriteFoodType)Enum.Parse(typeof(FavoriteFoodType), Get(s, "food"), true);
                    if (Has(s, "music")) sd.FavoriteMusic = (FavoriteMusicType)Enum.Parse(typeof(FavoriteMusicType), Get(s, "music"), true);
                    if (Has(s, "color"))
                        for (int i = 0; i < CASCharacter.GetNumberOfFavoriteColors(); i++)
                            if (string.Equals(CASCharacter.GetFavoriteColorNameByIndex(i), Get(s, "color"), StringComparison.OrdinalIgnoreCase)) sd.FavoriteColor = CASCharacter.GetFavoriteColorByIndex(i);
                    if (Has(s, "zodiac")) sd.Zodiac = (Zodiac)Enum.Parse(typeof(Zodiac), Get(s, "zodiac"), true);
                    if (Has(s, "voice")) sd.VoiceVariation = (VoiceVariationType)Enum.Parse(typeof(VoiceVariationType), Get(s, "voice"), true);
                    h.Add(sd);
                    byKey[Get(s, "key")] = sd;
                }
                foreach (object o in rel)
                {
                    Dictionary<string, object> x = o as Dictionary<string, object>;
                    SimDescription p, q;
                    if (x == null || !byKey.TryGetValue(Get(x, "a"), out p) || !byKey.TryGetValue(Get(x, "b"), out q)) throw new McpException("relation: unknown Sim key");
                    string type = Get(x, "type");
                    if (type == "parent") { p.Genealogy.AddChild(q.Genealogy); CasRelationship(p, q, LongTermRelationshipTypes.GoodFriend); }
                    else if (type == "sibling") { if (!Genealogy.IsSibling(p.Genealogy, q.Genealogy)) p.Genealogy.AddSibling(q.Genealogy); CasRelationship(p, q, LongTermRelationshipTypes.Friend); }
                    else if (type == "spouse") { p.Genealogy.Marry(q.Genealogy); CasRelationship(p, q, LongTermRelationshipTypes.Spouse); }
                    else if (type == "partner") CasRelationship(p, q, LongTermRelationshipTypes.Partner);
                    else if (type == "roommate") CasRelationship(p, q, LongTermRelationshipTypes.Friend);
                    else throw new McpException("relation type " + type + ": spouse|partner|parent|sibling|roommate");
                }
                List<ISimDescription> members = new List<ISimDescription>();
                foreach (SimDescription sd in byKey.Values) members.Add(sd);
                int casFunds = CASLogic.GetHouseholdStartingFunds(members), funds = casFunds;
                if (a.Has("funds")) { funds = (int)a.Double("funds"); if (funds <= 0) throw new McpException("funds must be > 0"); }
                h.SetFamilyFunds(funds);
                r["cas_funds"] = casFunds;
                r["funds_cheat"] = funds != casFunds;
                int couples = 0, toddlers = 0;
                foreach (object o in rel) { Dictionary<string, object> x = o as Dictionary<string, object>; string ty = x == null ? null : Get(x, "type"); if (ty == "spouse" || ty == "partner") couples++; }
                foreach (SimDescription sd in byKey.Values) if (sd.Age == CASAgeGenderFlags.Toddler) toddlers++;
                Need need = new Need(members.Count, couples, toddlers);
                if (sEssentials != null && sEssentials.Count < 7) sEssentials = null;
                MoveInPlan plan;
                if (a.Bool("empty_lot", false))
                {
                    // build_layout's house: the cheapest vacant empty residential lot at least 16 x 16 tiles the funds buy; nothing to furnish
                    plan = null;
                    foreach (object o in LotManager.AllLots)
                    {
                        Lot l = o as Lot;
                        if (l == null || l.IsWorldLot || !l.IsResidentialLot || l.IsApartmentLot || l.IsDormitoryLot || l.Household != null || !World.LotIsEmpty(l.LotId)) continue;
                        Corners k = l.Corners;
                        Vector2 o0 = k.GetVector2Corner(Corner.Origin);
                        if (l.Cost > funds || (k.GetVector2Corner(Corner.XAxis) - o0).Length() < 15.5f || (k.GetVector2Corner(Corner.YAxis) - o0).Length() < 15.5f) continue;
                        if (plan == null || l.Cost < plan.Price) { plan = new MoveInPlan(); plan.Lot = l; plan.Furnished = true; plan.Price = l.Cost; }
                    }
                    if (plan == null) throw new McpException("no vacant empty residential lot of 16 x 16 tiles or more for " + funds);
                }
                else if (a.Has("lot"))
                {
                    Lot given = LotManager.GetLot(a.ULong("lot"));
                    if (given == null || !Vacant(given)) throw new McpException("lot " + (given != null ? given.Name : a.Str("lot")) + " is not a vacant residential lot with a house");
                    plan = Plan(given, given.Cost <= funds, need);
                    if (plan.Total > funds) plan = Plan(given, false, need);
                    if (plan.Total > funds) throw new McpException("funds: " + funds + " do not buy " + given.Name + " (" + plan.Describe() + ")");
                    string misfit = FitFails(plan);
                    if (misfit != null) throw new McpException("fit: " + given.Name + " has no room in the house for " + misfit);
                }
                else
                {
                    // the best affordable lot whose house has room for the missing essentials (chain157: beds found no spot);
                    // a lot that cannot fit them -> the next affordable one, up to kFitLots tested
                    plan = null;
                    string misfits = "";
                    int tested = 0;
                    foreach (MoveInPlan c in PickMoveIn(funds, need))
                    {
                        string misfit = FitFails(c);
                        if (misfit == null) { plan = c; break; }
                        misfits += (misfits.Length > 0 ? "; " : "") + c.Lot.Name + ": " + misfit;
                        if (++tested == kFitLots) break;
                    }
                    if (plan == null) throw new McpException("fit: no affordable house has room for the essentials (" + misfits + ")");
                    if (misfits.Length > 0) r["fit_skipped"] = misfits;
                }
                Lot lot = plan.Lot;
                r["lot"] = lot.Name;
                r["lot_id"] = lot.LotId.ToString();
                r["lot_cost"] = plan.Price;
                r["furnished"] = plan.Furnished;
                r["starting_funds"] = funds;
                List<object> shop = new List<object>();
                foreach (KeyValuePair<string, BuildBuyProduct> b in plan.Buy)
                {
                    Dictionary<string, object> d = Util.Obj();
                    d["need"] = b.Key;
                    d["product"] = KeyString(b.Value.ProductResourceKey);
                    d["name"] = b.Value.CatalogName;
                    d["price"] = (int)b.Value.Price;
                    // the next cheapest products of this kind, cheapest first: createHousehold tries them when a buy fails
                    List<object> alts = new List<object>();
                    foreach (BuildBuyProduct ap in Essentials()[b.Key])
                    {
                        if (ap == b.Value) continue;
                        Dictionary<string, object> ad = Util.Obj();
                        ad["product"] = KeyString(ap.ProductResourceKey);
                        ad["name"] = ap.CatalogName;
                        ad["price"] = (int)ap.Price;
                        alts.Add(ad);
                    }
                    d["alternates"] = alts;
                    shop.Add(d);
                }
                r["buy"] = shop;
                r["buy_cost"] = plan.Shop;
                if (dry) { r["dry"] = true; Discard(h, byKey); return r; }
                // BinCommon is internal to the game assembly: its two static methods by reflection
                Type bin = typeof(Household).Assembly.GetType("Sims3.Gameplay.BinCommon");
                MethodInfo pay = bin == null ? null : bin.GetMethod("PayForLot", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo move = bin == null ? null : bin.GetMethod("CreateAndMoveIn", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (pay == null || move == null) throw new McpException("BinCommon.PayForLot / CreateAndMoveIn not found");
                if (!(bool)pay.Invoke(null, new object[] { h, lot, plan.Furnished })) throw new McpException("funds: " + funds + " do not buy " + lot.Name + " (" + plan.Price + ")");
                // unfurnished: the game's own step from BinCommon.PlaceFromExportBin (MakeLotUnfurnished, then the cached values)
                if (!plan.Furnished) { lot.MakeLotUnfurnished(); lot.UpdateCachedValues(); }
                move.Invoke(null, new object[] { h, lot });
            }
            catch (Exception)
            {
                if (h.LotHome == null) Discard(h, byKey);
                throw;
            }
            List<object> made = new List<object>();
            foreach (KeyValuePair<string, SimDescription> kv in byKey)
            {
                Dictionary<string, object> d = Util.Obj();
                d["key"] = kv.Key;
                d["id"] = Util.Id(kv.Value.SimDescriptionId);
                d["name"] = kv.Value.FullName;
                List<object> traits = new List<object>();
                foreach (Trait t in kv.Value.TraitManager.List) if (t.IsVisible) traits.Add(t.Guid.ToString());
                d["traits"] = traits;
                d["wish"] = kv.Value.LifetimeWish;
                made.Add(d);
            }
            r["household"] = h.Name;
            r["household_id"] = Util.Id(h.HouseholdId);
            r["funds_after"] = h.FamilyFunds;
            r["sims"] = made;
            return r;
        }

        // nothing moved in: the descriptions and the household go again (the game's own dispose path)
        static void Discard(Household h, Dictionary<string, SimDescription> byKey)
        {
            foreach (SimDescription sd in byKey.Values) { if (sd.Household == h) h.Remove(sd); sd.Dispose(); }
            h.Dispose();
        }

        // Move-in (docs/181 §3.1). The player's move-in has no beds or fridge test [verified: PlayFlowModel.PlayBinHousehold ->
        // BinCommon.PlaceFromExportBin and Edit Town's EditTownPlaceTool.PlaceHousehold check only BinCommon.PayForLot, i.e.
        // funds >= Lot.Cost furnished or Lot.GetUnfurnishedCost unfurnished; Lot.CanSupportPopulation is only the in-game Move
        // dialog and story progression]. So the hard rule is the price; the essentials a player then buys in Buy mode are our
        // choice: a bed spot per Sim (doubles for couples), a crib per Toddler, a fridge, a toilet, a shower or tub, a stove.
        class Need
        {
            public int Spots, Couples, Cribs;
            public Need(int sims, int couples, int toddlers) { Spots = sims - toddlers; Couples = couples; Cribs = toddlers; }
        }

        class MoveInPlan
        {
            public Lot Lot; public bool Furnished; public int Price, Shop;
            public List<KeyValuePair<string, BuildBuyProduct>> Buy = new List<KeyValuePair<string, BuildBuyProduct>>();
            public string Missing; // an essential the catalog has no product for
            public int Total { get { return Missing != null ? int.MaxValue : Price + Shop; } }
            public string Describe() { return (Furnished ? "furnished " : "unfurnished ") + Price + " + essentials " + (Missing != null ? "(no " + Missing + " in the catalog" + (sEssentialFailures.Length > 0 ? "; " + sEssentialFailures : "") + ")" : Shop.ToString()); }
        }

        // a lot a household can live on: vacant, residential, not an apartment or dorm, a house on it (an empty lot needs building)
        static bool Vacant(Lot l)
        {
            return l != null && !l.IsWorldLot && l.IsResidentialLot && !l.IsApartmentLot && !l.IsDormitoryLot && l.Household == null && !World.LotIsEmpty(l.LotId);
        }

        // the price and the shopping list for a lot bought furnished or unfurnished; unfurnished keeps what Lot.MakeLotUnfurnished
        // keeps (StaysAfterEvict, public or hidden residential rooms); beds count by NumberOfSpots like Lot.GetLotMetrics
        static MoveInPlan Plan(Lot l, bool furnished, Need need)
        {
            MoveInPlan p = new MoveInPlan();
            p.Lot = l; p.Furnished = furnished; p.Price = furnished ? l.Cost : l.GetUnfurnishedCost();
            int spots = 0, cribs = 0;
            Dictionary<string, bool> have = new Dictionary<string, bool>();
            foreach (GameObject o in l.GetObjects<GameObject>())
            {
                if (!furnished && !o.StaysAfterEvict() && !o.IsInPublicResidentialRoom && !o.IsInHiddenResidentialRoom) continue;
                IBed bed = o as IBed;
                if (bed != null && !(o is IBotBed)) spots += bed.NumberOfSpots();
                if (o is ICrib) cribs++;
                string k = EssentialKind(o);
                if (k != null) have[k] = true;
            }
            int deficit = Math.Max(0, need.Spots - spots), doubles = Math.Min(need.Couples, deficit / 2);
            Add(p, "bed_double", doubles);
            Add(p, "bed_single", deficit - 2 * doubles);
            Add(p, "crib", Math.Max(0, need.Cribs - cribs));
            foreach (string k in new string[] { "fridge", "toilet", "shower", "stove" }) if (!have.ContainsKey(k)) Add(p, k, 1);
            return p;
        }

        static void Add(MoveInPlan p, string kind, int n)
        {
            if (n <= 0) return;
            List<BuildBuyProduct> prods;
            if (!Essentials().TryGetValue(kind, out prods)) { p.Missing = kind; return; }
            for (int i = 0; i < n; i++) { p.Buy.Add(new KeyValuePair<string, BuildBuyProduct>(kind, prods[0])); p.Shop += (int)prods[0].Price; }
        }

        // the lots a player picks with this budget, best first: furnished lots that already have every essential (most expensive
        // first), then the houses whose price (furnished if affordable, else unfurnished) plus the essentials fits the funds (most
        // expensive first). The caller takes the first whose house has room for what it must buy (FitFails).
        static List<MoveInPlan> PickMoveIn(int funds, Need need)
        {
            List<MoveInPlan> ready = new List<MoveInPlan>(), fits = new List<MoveInPlan>();
            MoveInPlan cheapest = null;
            foreach (object o in LotManager.AllLots)
            {
                Lot l = o as Lot;
                if (!Vacant(l) || l.GetUnfurnishedCost() > funds) continue;
                MoveInPlan p = l.Cost <= funds ? Plan(l, true, need) : null;
                if (p != null && p.Buy.Count == 0 && p.Missing == null) { ready.Add(p); continue; }
                if (p == null || p.Total > funds) { MoveInPlan u = Plan(l, false, need); if (p == null || u.Total < p.Total) p = u; }
                if (cheapest == null || p.Total < cheapest.Total) cheapest = p;
                if (p.Total <= funds) fits.Add(p);
            }
            Comparison<MoveInPlan> dearFirst = delegate (MoveInPlan x, MoveInPlan y) { return y.Lot.Cost.CompareTo(x.Lot.Cost); };
            ready.Sort(dearFirst);
            fits.Sort(dearFirst);
            ready.AddRange(fits);
            if (ready.Count > 0) return ready;
            // the caller (story-households.ts) tries a smaller generated household on "funds:"
            throw new McpException("funds: " + funds + " simoleons buy no vacant house with the essentials for " + (need.Spots + need.Cribs) + " Sims"
                + (cheapest != null ? " (cheapest: " + cheapest.Lot.Name + ", " + cheapest.Describe() + ")" : " (no vacant house within the unfurnished price)"));
        }

        const int kFitLots = 8, kPlaceTries = 40;

        // null when every product the plan buys fits in the house, else the first that does not ("bed_double (Name)"). The test
        // objects are placed one after another (each blocks the next), then destroyed. Unfurnished: what MakeLotUnfurnished will
        // remove is ignored by the placement search. A product the game cannot create is not a fit failure (createHousehold
        // tries its alternates).
        static string FitFails(MoveInPlan p)
        {
            if (p.Buy.Count == 0) return null;
            List<ObjectGuid> leaving = new List<ObjectGuid>();
            if (!p.Furnished)
                foreach (GameObject o in p.Lot.GetObjects<GameObject>())
                    if (!o.StaysAfterEvict() && !o.IsInPublicResidentialRoom && !o.IsInHiddenResidentialRoom) leaving.Add(o.ObjectId);
            ObjectGuid[] ignore = leaving.Count > 0 ? leaving.ToArray() : null;
            List<GameObject> placed = new List<GameObject>();
            try
            {
                foreach (KeyValuePair<string, BuildBuyProduct> b in p.Buy)
                {
                    GameObject go;
                    try { go = GlobalFunctions.CreateObjectOutOfWorld(b.Value.ProductResourceKey) as GameObject; }
                    catch (Exception) { continue; }
                    if (go == null) continue;
                    if (!PlaceInHouse(go, p.Lot, ignore)) { go.Destroy(); return b.Key + " (" + b.Value.CatalogName + ")"; }
                    go.AddToWorld();
                    placed.Add(go);
                }
                return null;
            }
            finally { foreach (GameObject g in placed) g.Destroy(); }
        }

        // the game's own in-house placement: GlobalFunctions.PlaceObjectOnLot's loop (Lot.GetRandomPosition inside rooms, then
        // World.FindGoodLocation with StayInRoom; magic gnomes and the vampire gnome use it inside-only), here with objects to
        // ignore and the lot checked. Sets the position and forward; the caller adds the object to the world.
        static bool PlaceInHouse(GameObject go, Lot lot, ObjectGuid[] ignore)
        {
            for (int i = 0; i < kPlaceTries; i++)
            {
                Vector3 start = lot.GetRandomPosition(true, false);
                if (start == Vector3.Invalid) continue;
                World.FindGoodLocationParams fgl = new World.FindGoodLocationParams(start);
                fgl.BooleanConstraints |= FindGoodLocationBooleans.StayInRoom;
                Vector3 pos, fwd;
                if (World.FindGoodLocation(go.ObjectId, fgl, ignore, out pos, out fwd) && LotManager.GetLotAtPoint(pos) == lot)
                {
                    go.SetPosition(pos);
                    go.SetForward(fwd);
                    return true;
                }
            }
            return false;
        }

        // the cheapest kAlternates catalog products per essential, cheapest first, classified by the object the game creates for
        // it (then destroyed). Catalog() is Buy mode's own list (UserToolUtils.GetObjectProductListFiltered, ShowInCatalog, a
        // buy category), so CC shows only when Buy mode shows it. A product the game cannot create out of world is skipped, not
        // fatal: chain156 full-house died on the Generations stroller (Kids > Misc, OBJD 0x98A509 group 0x38000000, StrollerCheap),
        // whose create came back without a proxy (guid 0, Simulator.CreateObject's ObjectManagerFailure path) [inferred cause].
        const int kAlternates = 3;
        static Dictionary<string, List<BuildBuyProduct>> sEssentials;
        static string sEssentialFailures = "";
        static Dictionary<string, List<BuildBuyProduct>> Essentials()
        {
            if (sEssentials != null) return sEssentials;
            List<BuildBuyProduct> c = new List<BuildBuyProduct>();
            foreach (BuildBuyProduct p in Catalog()) if (!p.IsWallObject && KindsFor(p) != null) c.Add(p);
            c.Sort(delegate (BuildBuyProduct x, BuildBuyProduct y) { return x.Price.CompareTo(y.Price); });
            Dictionary<string, List<BuildBuyProduct>> d = new Dictionary<string, List<BuildBuyProduct>>();
            int full = 0, failed = 0;
            string fails = "";
            foreach (BuildBuyProduct p in c)
            {
                if (full == 7) break;
                // create only products that can still fill a kind: once its kinds hold kAlternates each, the group is skipped
                bool open = false;
                foreach (string k0 in KindsFor(p)) { List<BuildBuyProduct> l0; if (!d.TryGetValue(k0, out l0) || l0.Count < kAlternates) open = true; }
                if (!open) continue;
                GameObject go;
                try { go = GlobalFunctions.CreateObjectOutOfWorld(p.ProductResourceKey) as GameObject; }
                catch (Exception e)
                {
                    // the game destroys the half-made object itself (Simulator.CreateObject) before GlobalFunctions throws
                    if (++failed <= 3) fails += (fails.Length > 0 ? "; " : "") + p.CatalogName + " " + KeyString(p.ProductResourceKey) + ": " + e.GetType().Name + " (" + Simulator.sCreateObjectError + ")";
                    continue;
                }
                if (go == null) continue;
                string k = EssentialKind(go);
                go.Destroy();
                if (k == null || Array.IndexOf(KindsFor(p), k) < 0) continue;
                List<BuildBuyProduct> l;
                if (!d.TryGetValue(k, out l)) d[k] = l = new List<BuildBuyProduct>();
                if (l.Count == kAlternates) continue;
                l.Add(p);
                if (l.Count == kAlternates) full++;
            }
            sEssentialFailures = failed == 0 ? "" : failed + " catalog products not creatable: " + fails;
            // an incomplete set is dropped at the next make_household: a creation refused in a passing game state is tried again
            sEssentials = d;
            return d;
        }

        // the essential kinds a catalog product may be, from Buy mode's own function sort (the product data, before any object is
        // made): Beds -> beds; Kids > Furniture or Beds -> crib (chain156: no crib was under Beds or Kids > Misc, so Buy mode
        // lists cribs under Kids > Furniture [inferred, live check I-181-10]); appliances -> fridge or stove; plumbing -> toilet or
        // shower. Kids > Misc (strollers, chain156's not-creatable Twinbrook Roller) never. The created object's class decides.
        static readonly string[] kBedKinds = { "bed_double", "bed_single" }, kCribKinds = { "crib" }, kCribBedKinds = { "crib", "bed_double", "bed_single" },
            kApplianceKinds = { "fridge", "stove" }, kPlumbingKinds = { "toilet", "shower" };
        static string[] KindsFor(BuildBuyProduct p)
        {
            ulong s = p.BuySubCategoryFlags;
            const ulong beds = (ulong)BuildBuyProduct.eBuySubCategory.kBuySubCategoryBeds;
            if ((s & (ulong)BuildBuyProduct.eBuySubCategory.kBuySubCategoryMiscellaneousKids) != 0) return null;
            if ((p.BuyCategoryFlags & (uint)BuildBuyProduct.eBuyCategory.kBuyCategoryKids) != 0)
                return (s & beds) != 0 ? kCribBedKinds : (s & (ulong)BuildBuyProduct.eBuySubCategory.kBuySubCategoryFurniture) != 0 ? kCribKinds : null;
            if ((s & beds) != 0) return kBedKinds;
            if ((s & (ulong)(BuildBuyProduct.eBuySubCategory.kBuySubCategoryLargeAppliances | BuildBuyProduct.eBuySubCategory.kBuySubCategoryMiscellaneousAppliances)) != 0) return kApplianceKinds;
            if ((s & (ulong)(BuildBuyProduct.eBuySubCategory.kBuySubCategoryToilets | BuildBuyProduct.eBuySubCategory.kBuySubCategoryShowersAndTubs
                | BuildBuyProduct.eBuySubCategory.kBuySubCategoryMiscellaneousPlumbing)) != 0) return kPlumbingKinds;
            return null;
        }

        // bunk beds are a container of two BedSingle parts (not an IBed): left out; bot beds are for Plumbots
        static string EssentialKind(GameObject o)
        {
            if (o is IBotBed) return null;
            if (o is BedDouble) return "bed_double";
            if (o is BedSingle) return "bed_single";
            if (o is ICrib) return "crib";
            if (o is Fridge) return "fridge";
            if (o is IToilet) return "toilet";
            if (o is IShower || o is IBathtub) return "shower";
            if (o is Stove) return "stove";
            return null;
        }

        // CASLogic.LTRForceChangeState's sequence: partner, first-kiss bit for romance, forced state, liking in the CAS default range
        static void CasRelationship(SimDescription a, SimDescription b, LongTermRelationshipTypes type)
        {
            Relationship rel = Relationship.Get(a, b, true);
            if (rel == null || rel.LTR.CurrentLTR == type) return;
            LTRData d = LTRData.Get(type);
            if (d.SetsPartner) a.SetPartner(b);
            if (d.IsRomantic) rel.LTR.AddInteractionBit(LongTermRelationship.InteractionBits.Kissed);
            rel.LTR.ForceChangeState(type);
            rel.LTR.SetLiking(RandomUtil.GetFloat(d.MinDefaultLikingFromCAS, d.MaxDefaultLikingFromCAS));
        }

        // CAS trait slots by age (TraitManager.NumTraitsForAge tunables): Toddler 2, Child 3, Teen 4, YA+ 5
        static int SlotsFor(CASAgeGenderFlags age)
        {
            if ((age & (CASAgeGenderFlags.YoungAdult | CASAgeGenderFlags.Adult | CASAgeGenderFlags.Elder)) != 0) return TraitManager.kTraitsForYoungAdultAndOlder;
            if (age == CASAgeGenderFlags.Teen) return TraitManager.kTraitsForTeens;
            if (age == CASAgeGenderFlags.Child) return TraitManager.kTraitsForChildren;
            return TraitManager.kTraitsForBabiesAndToddlers;
        }

        static List<TraitNames> TraitList(Dictionary<string, object> s)
        {
            List<TraitNames> l = new List<TraitNames>();
            List<object> raw = s.ContainsKey("traits") ? s["traits"] as List<object> : null;
            if (raw != null) foreach (object o in raw) l.Add(Util.ParseEnum<TraitNames>(Convert.ToString(o)));
            return l;
        }

        static CASAgeGenderFlags ParseFlag(string v) { return (CASAgeGenderFlags)Enum.Parse(typeof(CASAgeGenderFlags), v, true); }
        static bool Has(Dictionary<string, object> d, string k) { return d.ContainsKey(k) && d[k] != null && Convert.ToString(d[k]) != ""; }
        static string Get(Dictionary<string, object> d, string k) { return Has(d, k) ? Convert.ToString(d[k]) : null; }
    }
}
