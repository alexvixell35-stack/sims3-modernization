namespace Ts3Fixes
{
    // HouseholdToo local exact path, piece 2 (docs/276 rank 5, docs/data/tier2-local-phase-handoff.md L1 + L2 pure part):
    // game-independent rules the local driver (piece 3) and the worker replay use. No hook, no game type, no behaviour change.
    // Self-check: tools/selftest/autshadowlocal. Everything here is read by code that runs only with switch autt2local AND
    // [Experimental] HouseholdToo=1 (both default 0); flag off = Post never answers kPost.
    public static class AutShadowLocalCore
    {
        // ---- post gate (B7 prefix, sim thread) ----
        // kPost = post a local request; every other code = the game's own B9 with the game's C2, counted by code.
        public const int kPost = 0, kOff = 1, kNotManager = 2, kForced = 3, kScopeHh = 4, kScopeUnknown = 5, kNoLocal = 6, kBusy = 7, kN = 8;

        // switchOn = autt2local; hhTooOn = [Experimental] HouseholdToo (Fixes.HouseholdTooOn); managerSearch = search type
        // Autonomy run by the manager task; forceLocal / overriddenLot / forceSolo = mForceRunLocalAutonomy /
        // mOverriddenLocalAutonomyLot != null / mForceScoreSoloInteractions (side entries, they need the answer synchronously);
        // runsLocal = ShouldRunLocalAutonomy; busy = a request in flight; why = SafeUfWhy of the actor (-1 = failed check);
        // lift = Fixes.HouseholdLift(actor). Household scope goes through only when lifted and the flag is on; not live, gone
        // and unknown scope (-1) never post (unknown counts as household and lift needs a live Sim).
        public static int Post(bool switchOn, bool hhTooOn, bool managerSearch, bool forceLocal, bool overriddenLot, bool forceSolo,
            bool runsLocal, bool busy, int why, bool lift)
        {
            if (!switchOn || !hhTooOn) return kOff;
            if (!managerSearch) return kNotManager;
            if (forceLocal || overriddenLot || forceSolo) return kForced;
            if (why == UnseenFallbackCore.kHousehold) { if (!lift) return kScopeHh; }
            else if (why != UnseenFallbackCore.kSeen && !UnseenFallbackCore.SeenNpc(why)) return kScopeUnknown;
            if (!runsLocal) return kNoLocal;
            return busy ? kBusy : kPost;
        }

        // ---- B9 enumeration order (Autonomy.ScoreInteractionsForLocalAutonomy, Steam 1.67, read) ----
        // Sources in EA's order; Sources() returns the bit mask of the ones B9 walks, the driver walks bits low to high.
        public const int kSrcLotMap = 0, kSrcOwnMap = 1, kSrcOtherSims = 2, kSrcInventory = 3, kSrcSituations = 4, kSrcSolo = 5, kSrcN = 6;

        // lotNull / lotWorld = the lot B9 picked (override, current lot, or B10's closest lot on the world lot); overridden =
        // mOverriddenLocalAutonomyLot set; actorNpc / actorLotWorld = mActor.IsNPC / mActor.LotCurrent.IsWorldLot (the inventory
        // test reads the actor's own lot, not the picked one); generic = c == None; alienBrain = c == AlienBrainPower.
        public static int Sources(bool lotNull, bool lotWorld, bool overridden, bool actorNpc, bool actorLotWorld, bool generic, bool alienBrain, bool forceSolo)
        {
            int m = 0;
            if (!lotNull)
            {
                m |= 1 << kSrcLotMap | 1 << kSrcOwnMap;
                if (!lotWorld || overridden) m |= 1 << kSrcOtherSims;  // every Sim on the lot except the actor
            }
            if (!(actorNpc && actorLotWorld)) m |= 1 << kSrcInventory;
            if (generic) m |= 1 << kSrcSituations;
            if (generic || alienBrain || forceSolo) m |= 1 << kSrcSolo;
            return m;
        }

        // B11 generic search (ScoreInteractions(map)): the commodities B12 runs per map, in order: every use value > 0 (the
        // scorer's dictionary order), then every desire below MaxY, or Energy when ConsiderSleepingEvenWhenNotSleepy. Repeats
        // stay (EA calls B12 again; the dedupe set drops the pairs). A commodity search runs B12 for c alone.
        public static int Commodities(int[] useKeys, float[] useValues, int useN, int[] desireKinds, float[] desireValues, float[] desireMaxY,
            int desireN, int energyKind, bool considerSleep, int[] outKinds)
        {
            int n = 0;
            for (int i = 0; i < useN; i++) if (useValues[i] > 0f && n < outKinds.Length) outKinds[n++] = useKeys[i];
            for (int i = 0; i < desireN && n < outKinds.Length; i++)
                if (desireValues[i] < desireMaxY[i] || (desireKinds[i] == energyKind && considerSleep)) outKinds[n++] = desireKinds[i];
            return n;
        }

        // ---- H8 local rule (L1) ----
        // Inside a local replay (worker and reference) UpdateCacheIfNeeded is a no-op only for the request's own Autonomy, with
        // the search cache on at the request, and the actor's C0 fields still equal to the request's copy; else abort `cache`.
        public static bool LocalCacheNoop(bool requestAutonomy, bool cacheActiveAtRequest, bool c0Same)
        {
            return AutShadowCore.T2CacheNoop(requestAutonomy, cacheActiveAtRequest) && c0Same;
        }

        // pair walk codes
        public const int kNew = 0, kDup = 1, kCap = 2;
        public const int kMaxLocalPairs = 8192;  // as the meta kMaxPairs; L0 data sizes it again in piece 5
    }

    // The 9 fields Autonomy.CacheValuesToOptimizeTestFunctions(mActor) fills at B7 (Steam 1.67, read: IsOnFire, BabyIsComing,
    // IsNeat, IsGuestAtAParty, ActorsGroupSituation, IsActorScubaDiving, IsFireOnLot, IsActorInTombRoom,
    // CachedSimHouseholdMembers). Copied at the request; Same() compares bools by value and the two references by identity
    // (another search of the same actor refilled them = a different list or situation object).
    public sealed class LocalC0
    {
        public bool OnFire, BabyComing, Neat, PartyGuest, Scuba, FireOnLot, TombRoom;
        public object GroupSituation, HouseholdMembers;

        public void CopyFrom(LocalC0 o)
        {
            OnFire = o.OnFire; BabyComing = o.BabyComing; Neat = o.Neat; PartyGuest = o.PartyGuest; Scuba = o.Scuba;
            FireOnLot = o.FireOnLot; TombRoom = o.TombRoom; GroupSituation = o.GroupSituation; HouseholdMembers = o.HouseholdMembers;
        }

        public bool Same(LocalC0 o)
        {
            return o != null && OnFire == o.OnFire && BabyComing == o.BabyComing && Neat == o.Neat && PartyGuest == o.PartyGuest
                && Scuba == o.Scuba && FireOnLot == o.FireOnLot && TombRoom == o.TombRoom
                && (object)GroupSituation == (object)o.GroupSituation && (object)HouseholdMembers == (object)o.HouseholdMembers;
        }
    }

    // Own dedupe set of the local replay (never the game's InteractionCheckTable: static node pools). Same key rule as
    // InteractionCheckTable.TryAdd [verified, Steam 1.67]: the pair's GetHashCode + Equals (InteractionObjectPair: definition
    // Equals and the same Target), first occurrence wins. Preallocated open addressing, no allocation per pair; Clear() drops
    // every reference (no destroyed object kept alive between requests). Cap: pairs past kMaxLocalPairs answer kCap (counted `lpcap` by the driver, the rest of the walk is skip).
    public sealed class LocalPairSet
    {
        readonly object[] mKeys;
        readonly int[] mHash;
        readonly int mMask, mCap;
        public int Count, Capped;

        public LocalPairSet(int cap)
        {
            int size = 16;
            while (size < cap * 2) size <<= 1;
            mKeys = new object[size]; mHash = new int[size];
            mMask = size - 1; mCap = cap;
        }

        public void Clear()
        {
            Count = 0; Capped = 0;
            System.Array.Clear(mKeys, 0, mKeys.Length);
        }

        // AutShadowLocalCore.kNew / kDup / kCap
        public int Add(object pair)
        {
            int h = pair.GetHashCode();
            int i = (h * -1640531535) & mMask;  // golden-ratio spread; EA's own buckets are h % 109
            while (mKeys[i] != null)
            {
                if (mHash[i] == h && mKeys[i].Equals(pair)) return AutShadowLocalCore.kDup;
                i = (i + 1) & mMask;
            }
            if (Count >= mCap) { Capped++; return AutShadowLocalCore.kCap; }
            mHash[i] = h; mKeys[i] = pair; Count++;
            return AutShadowLocalCore.kNew;
        }
    }
}
