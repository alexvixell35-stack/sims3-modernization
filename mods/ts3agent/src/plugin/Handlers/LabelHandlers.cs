using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Sims3.Gameplay.ActorSystems;
using Sims3.Gameplay.Skills;
using Sims3.SimIFace;
using Sims3.SimIFace.BuildBuy;
using Sims3.SimIFace.CAS;

namespace Ts3Agent
{
    // Label check for the Sims 3 Performance Patch LocalizedStringCache (docs/17 §8.3, §9): S3PP memoizes
    // LocStrSvc_GetString_A, the native body behind IStringTable.GetLocalizedString(ulong), keyed on the key alone and
    // invalidated only when the service pointer or *(service+0x48) changes. A stale or wrong entry shows as a blank
    // label (a key looked up before its STBL loaded keeps answering "") or a wrong one. label_check resolves a fixed,
    // sorted sample from many sources and returns counts plus a hash, so a load with S3PP on and one with it off can be
    // compared: same install and language -> same hash and counts.
    // Two paths per key where the game offers both: the string path (StringTable.GetLocalizedString(string), not the
    // hooked body as far as the S3PP source says) and the ulong path (GetLocalizedString(HashString64(key)), or
    // CatalogNameKey for buy objects), which is the hooked one. "mismatch" = both resolved but differ; "hash_empty" =
    // string path resolved, ulong path blank (the stale-miss signature). Missing keys come back as "" (StringTable).
    public static partial class Handlers
    {
        static void RegisterLabels()
        {
            Dispatcher.Register("label_check", LabelCheck);
        }

        // Fixed samples: every Nth sorted literal key in the decompiled UI / Gameplay / StoreObjects code.
        static readonly string[] kUiKeys = {
            "Ui/Caption/BadgeTab:CompletedWallPost", "Ui/Caption/CAF/AddRelationship:Spouse", "Ui/Caption/CAS/ClickOnSim:ChangeShoes",
            "Ui/Caption/CAS/Loadscreen:CABCaption", "Ui/Caption/CAS/Traits:TraitsRemoved2", "Ui/Caption/CreateAStyle:ProcessingMaterials",
            "Ui/Caption/FeaturedStoreItem:Price", "Ui/Caption/GameEntry/EditTown:FundsWarning",
            "Ui/Caption/GameEntry/MainMenu/DiscAuthenticationFailure/EP03:Description", "Ui/Caption/GameEntry/MainMenu:CopyTown",
            "Ui/Caption/GameEntry/PlaceEPLotsWizard:BulldozePrompt", "Ui/Caption/Global/WorldName/EP11:Future", "Ui/Caption/Global:Share",
            "Ui/Caption/Global:Ellipsis", "Ui/Caption/Global:Comma", "Ui/Caption/HUD/CelebrityJournal:DialogTitle",
            "Ui/Caption/HUD/KnownInfoDialog:IsRich", "Ui/Caption/HUD/MotivesPanel:MotiveDescriptionBladderFrankenstein",
            "Ui/Caption/HUD/PetAdoptionInfoTooltip:Cost", "Ui/Caption/Hud/CareerPanel:GameStartsIn", "Ui/Caption/Hud/Navigation:OnVacationNoWork",
            "Ui/Caption/InGameWall/SocialNetwork:ShareFailed", "Ui/Caption/Install:MissingEPDependency", "Ui/Caption/MainMenu:Credits",
            "Ui/Caption/MovingDialog:Merge", "Ui/Caption/ObjectPicker:BaitTooltip", "Ui/Caption/ObjectPicker:Insects",
            "Ui/Caption/OpportunityConfirmDialog:ConfirmCase", "Ui/Caption/Options:ConnectedDialog", "Ui/Caption/Options:ReflectionQualityMed",
            "Ui/Caption/Party:Feast", "Ui/Caption/Party:VictoryParty", "Ui/Caption/Passport/Wizard:Private", "Ui/Caption/Passport:ParkChallenges",
            "Ui/Caption/PromptedSave:Continue", "Ui/Caption/RealEstateDialog/Button:Sell", "Ui/Caption/ResortReviewDialog:NoGym",
            "Ui/Caption/Shopping/Cart:Price", "Ui/Caption/SocialFeatures/Friends:SearchResults", "Ui/Caption/SocialFeatures:TimeToday",
        };
        static readonly string[] kInteractionKeys = {
            "Gameplay/Abstracts/CommonDoor/EnterDoor:InteractionName", "Gameplay/Abstracts/MinorPet/Watch:InteractionName",
            "Gameplay/Abstracts/ScriptObject/FoodStandDoItYourself:InteractionName", "Gameplay/Abstracts/ScriptObject/PutInInventory:InteractionName",
            "Gameplay/Abstracts/Windows/LookInWindow:InteractionName", "Gameplay/ActorSystems/ServoBot:ServoBotRepairInteractionName",
            "Gameplay/Actors/Sim/CallInviteOver:InteractionName", "Gameplay/Actors/Sim/PickUpToddler:InteractionName",
            "Gameplay/Actors/Sim/SitOnSim:InteractionName", "Gameplay/Core/CheckUpgradeRequirements:InteractionName",
            "Gameplay/Core/Terrain/DogPeeHere:InteractionName", "Gameplay/GameObjects/Miscellaneous/Newspaper/CheckWeatherNewspaper:InteractionName",
            "Gameplay/Objects/Beds/CribBase/PutCarriedChildInCribNoChild:InteractionName", "Gameplay/Objects/Core/Mailbox/TriggerStray:InteractionName",
            "Gameplay/Objects/Electronics/Computer/PlayChess:InteractionName", "Gameplay/Objects/Environment/CarTwizy:ViewInteractionName",
            "Gameplay/Objects/Fishing:MountedFishViewInteractionName", "Gameplay/Objects/MinorPetTerrarium/PlayWith:InteractionName",
            "Gameplay/Objects/Miscellaneous/TrashcanIndoor/EmptyTrash:InteractionName", "Gameplay/Objects/Plumbing/Toilet/UseToilet:InteractionName",
            "Gameplay/Objects/RabbitHoles/CityHall:VisitWithInteractionName", "Gameplay/Objects/RabbitHoles/Hospital/GetMedicalAdvice:InteractionName",
            "Gameplay/Objects/RabbitHoles/MovieSet:VisitWithInteractionName", "Gameplay/Objects/RabbitHoles/Subway/TravelToWith:InteractionName",
            "Gameplay/Objects/Rewards/MoodModifier/Cure:InteractionName", "Gameplay/Objects/Spawners/Relic/ReadInscription:InteractionName",
            "Gameplay/Objects/Vehicles/CarOwnable/VisitLot:InteractionName", "Gameplay/Passport/Passport:GetInCarReturnHomeInteractionName",
            "Gameplay/Pools/SwimHere:KickingInteractionName", "Gameplay/Skills/Fishing:LevelName", "Gameplay/Skills/RidingSkill:SkillsLevelName",
        };
        static readonly string[] kStoreScriptKeys = {
            "Gameplay/Objects/Beds/BedSleep:SleepStage", "Gameplay/Objects/Electronics/Stereo/DanceSolo:AwesomeInteractionName",
            "Gameplay/Objects/Electronics/Stereo/DanceSolo:ClubDaredevilInteractionName", "Gameplay/Objects/Electronics/Stereo/DanceSolo:EvilInteractionName",
            "Gameplay/Objects/Electronics/Stereo/DanceSolo:NeedMusicToDance", "Gameplay/Objects/Fishing:Fish", "Gameplay/Objects/FoodObjects:ContinueCooking",
            "Gameplay/Objects/RabbitHoles/Bookstore:Bookstore", "Gameplay/Objects/RabbitHoles/Restaurant:Restaurant",
            "Gameplay/Objects/Register/ShoppingRegister:ConsignedObjectsInfoWarning", "Gameplay/Objects/Register/ShoppingRegister:UserConsignedItem",
        };

        // Own delegate types: Action<,> with 2+ args lives in System.Core, which the game does not ship.
        delegate void LabelAdd(string src, string key, string v, string hashed);
        delegate void LabelKeys(string src, string[] ks);

        class LabelTally
        {
            public int n, empty, placeholder, mismatch, hashEmpty;
            public ulong hash = 14695981039346656037UL;
        }

        static void Fnv(ref ulong h, string s)
        {
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            h ^= '\n'; h *= 1099511628211UL;
        }

        // A raw key or the ToggleStringID "[text | key]" form shown instead of text.
        static bool IsPlaceholder(string v, string key)
        {
            return v == key || v.StartsWith("Gameplay/") || v.StartsWith("Ui/") || v.StartsWith("0x")
                || (v.StartsWith("[") && v.IndexOf(" | ") > 0);
        }

        // label_check {per?: 100 objects/parts per origin, dump?: false (also return every value, for a per-key diff),
        //   bench?: 0 (rounds of a timing loop over the sample's ulong keys plus as many keys no table has; Apex
        //   StringTableIndex A/B, docs/301): bench_ms / bench_n / bench_us = the loop's wall time (DateTime.UtcNow), lookups,
        //   us per lookup; bench_miss_us the same for the missing keys alone}
        static object LabelCheck(Args a)
        {
            int per = (int)a.Long("per", 100);
            bool dump = a.Bool("dump", false);
            int bench = (int)a.Long("bench", 0);
            List<ulong> benchKeys = new List<ulong>();
            Dictionary<string, LabelTally> tallies = new Dictionary<string, LabelTally>();
            LabelTally all = new LabelTally();
            List<object> problems = new List<object>(), values = new List<object>();

            // hashed == null: the source has no ulong path to compare.
            LabelAdd add = delegate(string src, string key, string v, string hashed)
            {
                LabelTally t;
                if (!tallies.TryGetValue(src, out t)) { t = new LabelTally(); tallies[src] = t; }
                v = v ?? "";
                string line = src + "\t" + key + "\t" + v + "\t" + (hashed ?? "-");
                string issue = null;
                foreach (LabelTally x in new LabelTally[] { t, all })
                {
                    x.n++;
                    Fnv(ref x.hash, line);
                    if (v.Length == 0) { x.empty++; issue = "empty"; }
                    else if (IsPlaceholder(v, key)) { x.placeholder++; issue = "placeholder"; }
                    if (hashed != null && v.Length > 0 && hashed.Length == 0) { x.hashEmpty++; issue = issue ?? "hash_empty"; }
                    else if (hashed != null && v.Length > 0 && hashed != v) { x.mismatch++; issue = issue ?? "mismatch"; }
                }
                if (issue != null && problems.Count < 20)
                {
                    Dictionary<string, object> d = Util.Obj();
                    d["src"] = src; d["key"] = key; d["issue"] = issue; d["value"] = v; d["hashed"] = hashed;
                    problems.Add(d);
                }
                if (dump) values.Add(line);
            };
            LabelKeys keys = delegate(string src, string[] ks)
            {
                foreach (string k in ks)
                {
                    ulong h = ResourceUtils.HashString64(k);
                    benchKeys.Add(h);
                    add(src, k, StringTable.GetLocalizedString(k), StringTable.GetLocalizedString(h));
                }
            };

            keys("ui", kUiKeys);
            keys("interactions", kInteractionKeys);
            keys("store_script", kStoreScriptKeys);

            List<string> traitKeys = new List<string>();
            foreach (Trait t in TraitManager.GetDictionaryTraits)
                if (t != null && !string.IsNullOrEmpty(t.TraitNameInfo)) traitKeys.Add(t.TraitNameInfo);
            traitKeys.Sort(StringComparer.Ordinal);
            keys("traits", traitKeys.ToArray());

            List<string> skillKeys = new List<string>();
            FieldInfo npd = typeof(Skill).GetField("mNonPersistableData", BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (Skill s in SkillManager.SkillDictionary)
            {
                object d = s == null ? null : npd.GetValue(s);
                string k = d == null ? null : d.GetType().GetField("Name").GetValue(d) as string;
                if (!string.IsNullOrEmpty(k)) skillKeys.Add(k);
            }
            skillKeys.Sort(StringComparer.Ordinal);
            keys("skills", skillKeys.ToArray());

            // Buy catalog, per origin: the native catalog name vs the hooked ulong path on its own key.
            Dictionary<string, int> taken = new Dictionary<string, int>();
            foreach (BuildBuyProduct p in SortedCatalog())
            {
                string o = ContentOrigin(p.ProductResourceKey);
                int c; taken.TryGetValue("obj_" + o, out c);
                if (c >= per) continue;
                taken["obj_" + o] = c + 1;
                benchKeys.Add(p.CatalogNameKey);
                add("objects_" + o, KeyString(p.ProductResourceKey), p.CatalogName, StringTable.GetLocalizedString(p.CatalogNameKey));
            }

            // CAS parts, per origin, sorted by key (cas_parts' order).
            List<CASPart> parts = new List<CASPart>();
            Dictionary<ResourceKey, bool> seen = new Dictionary<ResourceKey, bool>();
            foreach (CASPart p in new PartSearch())
                if (!seen.ContainsKey(p.Key)) { seen[p.Key] = true; parts.Add(p); }
            parts.Sort(delegate(CASPart x, CASPart y) { return string.CompareOrdinal(KeyString(x.Key), KeyString(y.Key)); });
            foreach (CASPart p in parts)
            {
                string o = ContentOrigin(p.Key);
                int c; taken.TryGetValue("cas_" + o, out c);
                if (c >= per) continue;
                taken["cas_" + o] = c + 1;
                add("cas_" + o, KeyString(p.Key), CASUtils.PartDataGetName(p.Key), null);
            }

            // Summary first: repro.ts prints only the first 160 characters of a step result.
            Dictionary<string, object> r = Util.Obj();
            r["hash"] = all.hash.ToString("x16", CultureInfo.InvariantCulture);
            r["total"] = all.n;
            r["empty"] = all.empty;
            r["placeholder"] = all.placeholder;
            r["mismatch"] = all.mismatch;
            r["hash_empty"] = all.hashEmpty;
            r["locale"] = StringTable.GetLocale();
            if (bench > 0 && benchKeys.Count > 0)
            {
                List<ulong> missing = new List<ulong>();
                foreach (ulong k in benchKeys) missing.Add(ResourceUtils.HashString64("ts3agent/label_check/missing:" + k.ToString("x16", CultureInfo.InvariantCulture)));
                int sink = 0;
                DateTime t0 = DateTime.UtcNow;
                for (int i = 0; i < bench; i++)
                {
                    foreach (ulong k in benchKeys) sink += (StringTable.GetLocalizedString(k) ?? "").Length;
                    foreach (ulong k in missing) sink += (StringTable.GetLocalizedString(k) ?? "").Length;
                }
                DateTime t1 = DateTime.UtcNow;
                for (int i = 0; i < bench; i++)
                    foreach (ulong k in missing) sink += (StringTable.GetLocalizedString(k) ?? "").Length;
                DateTime t2 = DateTime.UtcNow;
                long n = (long)bench * (benchKeys.Count + missing.Count);
                double ms = (t1 - t0).TotalMilliseconds, missMs = (t2 - t1).TotalMilliseconds;
                r["bench_ms"] = Math.Round(ms, 1);
                r["bench_n"] = n;
                r["bench_us"] = Math.Round(ms * 1000.0 / n, 3);
                r["bench_miss_us"] = Math.Round(missMs * 1000.0 / ((long)bench * missing.Count), 3);
                r["bench_sink"] = sink;
            }
            Dictionary<string, object> bySrc = Util.Obj();
            foreach (KeyValuePair<string, LabelTally> kv in tallies)
            {
                Dictionary<string, object> d = Util.Obj();
                d["n"] = kv.Value.n; d["empty"] = kv.Value.empty; d["placeholder"] = kv.Value.placeholder;
                d["mismatch"] = kv.Value.mismatch; d["hash_empty"] = kv.Value.hashEmpty;
                d["hash"] = kv.Value.hash.ToString("x16", CultureInfo.InvariantCulture);
                bySrc[kv.Key] = d;
            }
            r["sources"] = bySrc;
            r["problems"] = problems;
            if (dump) r["values"] = values;
            return r;
        }
    }
}
