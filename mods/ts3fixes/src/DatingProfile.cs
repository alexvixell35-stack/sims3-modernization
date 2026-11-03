using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Utilities;

namespace Ts3Fixes
{
    // datingprofile (docs/528, issue #175): EA's OnlineDatingManager.GetRecordForSim adds an empty record for any Sim it is
    // asked about, and DidSimsMeetOnline asks about both Sims (CelebrityManager.CanSocialize: every social with a celebrity
    // who is not a friend, autonomy included; InviteToLotSituation.MeetUp.Init). SimHasProfile only checks that a record
    // exists, so such a Sim "has a profile": the computer offers Browse, Check Messages and Remove, Edit builds a blank
    // profile, and Remove lasts only until the next lookup makes the record again [verified in the IL]. Fix, default off:
    // SimHasProfile is true only for a record with a Profile (this also covers the empty records already in a save, so
    // nothing scans them at load), and DidSimsMeetOnline on the sim thread reads the records without making one. That
    // second part lives in AutShadow's H4 hook (AutDatingHook.MetHook): one patch per method. ApplyAutShadow installs
    // the managed hooks at every start, whatever the native [AutShadow] section says, so it runs unless AutShadow's member
    // lookup failed. Counters [2051] SimHasProfile answers turned false, [2052] DidSimsMeetOnline calls answered
    // without making a record.
    public static partial class Fixes
    {
        static readonly FieldInfo sDatingRecords = typeof(OnlineDatingManager).GetField("mActiveRecords", kInstAll);
        static readonly FieldInfo sDatingProfile = DatingRecordField("Profile");

        static FieldInfo DatingRecordField(string name)
        {
            Type rec = typeof(OnlineDatingManager).GetNestedType("OnlineDatingRecord", BindingFlags.NonPublic);
            return rec != null ? rec.GetField(name, kInstAll) : null;
        }

        static IDictionary DatingRecords()
        {
            OnlineDatingManager m = OnlineDatingManager.Instance;
            return m != null && sDatingRecords != null ? sDatingRecords.GetValue(m) as IDictionary : null;
        }

        public static class DatingProfileHook
        {
            public static bool HasHook(ulong simDescId)
            {
                bool has = HasOrig(simDescId);
                if (!has || sDatingProfile == null || !ShortcutOn("datingprofile")) return has;
                IDictionary recs = DatingRecords();
                object rec = recs != null && recs.Contains(simDescId) ? recs[simDescId] : null;
                if (rec == null || sDatingProfile.GetValue(rec) != null) return has;
                PatchCounters()[2051]++;
                return false;
            }
            public static bool HasOrig(ulong simDescId) { return false; } // receives OnlineDatingManager.SimHasProfile's original body
        }

        // Harness check (repro dating-profile-check). "expect broken" (switch off): DidSimsMeetOnline on two Sims with no
        // record (not of the active household) adds both records, and SimHasProfile is then true. "expect fixed" (switch on,
        // after the off arm): the empty records the off arm left read as no profile, and DidSimsMeetOnline on two more such
        // Sims adds no record (AutShadow's H4 hook in place) or adds records that still read as no profile (H4 not installed;
        // the line says which). The fixed arm then removes every record the check added that is still empty.
        static readonly List<ulong> sCheckIds = new List<ulong>();

        public static object DatingProfileCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "datingprofile <expect broken|expect fixed>";
            if (!OnlineDatingManager.IsActive) throw new InvalidOperationException("datingprofile check: online dating is not active");
            IDictionary recs = DatingRecords();
            if (recs == null || sDatingProfile == null) throw new InvalidOperationException("datingprofile check: no record table");
            ulong a = 0, b = 0;
            foreach (Household h in Household.sHouseholdList)
            {
                if (h == null || h == Household.ActiveHousehold) continue;
                foreach (SimDescription sd in h.AllSimDescriptions)
                {
                    ulong id = sd != null ? sd.SimDescriptionId : 0;
                    if (id == 0 || recs.Contains(id) || sCheckIds.Contains(id)) continue;
                    if (a == 0) a = id; else if (id != a) { b = id; break; }
                }
                if (b != 0) break;
            }
            if (b == 0) throw new InvalidOperationException("datingprofile check: no two Sims without a record");
            bool on = ShortcutOn("datingprofile");
            int before = recs.Count;
            bool met = OnlineDatingManager.DidSimsMeetOnline(a, b);
            int added = recs.Count - before;
            bool hasA = OnlineDatingManager.SimHasProfile(a), hasB = OnlineDatingManager.SimHasProfile(b);
            sCheckIds.Add(a); sCheckIds.Add(b);
            string line = "met " + met + ", records added " + added + ", SimHasProfile " + hasA + "/" + hasB + " (switch " + on + ")";
            bool ok;
            if (what == "expect broken") ok = !on && added == 2 && hasA && hasB;
            else
            {
                int oldEmpty = 0, oldSaid = 0, removed = 0;
                foreach (ulong id in sCheckIds)
                {
                    if (id == a || id == b || !recs.Contains(id)) continue;
                    oldEmpty++;
                    if (OnlineDatingManager.SimHasProfile(id)) oldSaid++;
                }
                foreach (ulong id in sCheckIds)
                    if (recs.Contains(id) && sDatingProfile.GetValue(recs[id]) == null) { recs.Remove(id); removed++; }
                sCheckIds.Clear();
                line += "; off arm's empty records " + oldEmpty + ", read as a profile " + oldSaid + ", empty records removed " + removed
                      + (added == 0 ? " (H4 in place: no record made)" : " (H4 not installed: records made, not a profile)");
                ok = on && !met && !hasA && !hasB && oldEmpty > 0 && oldSaid == 0 && (added == 0 || added == 2);
            }
            if (!ok) throw new InvalidOperationException("datingprofile " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
