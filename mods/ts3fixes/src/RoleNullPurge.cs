using System;
using System.Collections.Generic;
using Sims3.Gameplay.Roles;

namespace Ts3Fixes
{
    // rolenullpurge (tuning kRoleNullPurge, off by default). RoleManagerTask.Simulate starts every pass with
    // RoleManager.VerifyRoleGivingObjectRoles, which walks mRoles[LocationMerchant] and reads each Role's RoleGivingObject;
    // UpdateAndGetRolesThatNeedPeople and the 30-minute SimulateRole loop walk every mRoles list the same way. A null list
    // or a null Role in mRoles (save data: EA's AddRole / RemoveRole never store either, so it comes from a role whose type
    // no longer loads, e.g. a removed mod) throws NullReferenceException there; the task catches only ResetException, so it
    // ends and no role (bartender, merchant, festival role...) is filled again until the next world load.
    // [sl1 2026-11-17, Starlight Shores: NRE in VerifyRoleGivingObjectRoles from RoleManagerTask; which null it was is
    // inferred, the two are the only nulls that method can meet with a non-null `this`.]
    // Fix: before EA's verify, null Roles are dropped from every list and null or emptied lists removed (EA removes a type's
    // key when its list empties). Counters [2775] null lists removed, [2776] null Roles removed, [2777] errors.
    public static partial class Fixes
    {
        public class RoleNullPurgeHook
        {
            public void VerifyHook()
            {
                if (ShortcutOn("rolenullpurge"))
                {
                    try { RolePurgeNulls(((RoleManager)(object)this).mRoles); }
                    catch (Exception) { PatchCounters()[2777]++; }
                }
                VerifyOrig();
            }
            public void VerifyOrig() { } // receives RoleManager.VerifyRoleGivingObjectRoles' original body
        }

        static bool RoleIsNull(Role r) { return r == null; }

        static void RolePurgeNulls(Dictionary<Role.RoleType, List<Role>> roles)
        {
            if (roles == null) return;
            List<Role.RoleType> drop = null;
            foreach (KeyValuePair<Role.RoleType, List<Role>> kv in roles)
            {
                if (kv.Value == null) PatchCounters()[2775]++;
                else
                {
                    int n = kv.Value.RemoveAll(RoleIsNull);
                    if (n == 0 || kv.Value.Count > 0) { PatchCounters()[2776] += n; continue; }
                    PatchCounters()[2776] += n;
                }
                if (drop == null) drop = new List<Role.RoleType>();
                drop.Add(kv.Key);
            }
            if (drop != null) foreach (Role.RoleType t in drop) roles.Remove(t);
        }

        // Harness check (repro rolenullpurge-check): puts a null Role into mRoles[LocationMerchant], then a null list there,
        // and calls EA's VerifyRoleGivingObjectRoles once each; mRoles is restored after each. "expect broken" (switch off)
        // wants a NullReferenceException both times; "expect fixed" (switch on) wants none.
        public static object RoleNullPurgeCheck(string what)
        {
            if (what != "expect broken" && what != "expect fixed") return "rolenullpurge <expect broken|expect fixed>";
            RoleManager rm = RoleManager.sRoleManager;
            if (rm == null || rm.mRoles == null) throw new InvalidOperationException("rolenullpurge check: no RoleManager");
            Role.RoleType t = Role.RoleType.LocationMerchant;
            string[] threw = { "nothing", "nothing" };
            for (int k = 0; k < 2; k++)
            {
                List<Role> had;
                bool hadKey = rm.mRoles.TryGetValue(t, out had);
                List<Role> copy = hadKey && had != null ? new List<Role>(had) : null;
                if (k == 0) { List<Role> l = copy != null ? new List<Role>(copy) : new List<Role>(); l.Add(null); rm.mRoles[t] = l; }
                else rm.mRoles[t] = null;
                try { rm.VerifyRoleGivingObjectRoles(); }
                catch (Exception e) { threw[k] = e.GetType().Name; }
                finally
                {
                    if (hadKey) { if (had != null) { had.Clear(); had.AddRange(copy); } rm.mRoles[t] = had; }
                    else rm.mRoles.Remove(t);
                }
            }
            bool on = ShortcutOn("rolenullpurge");
            string line = "null Role: threw " + threw[0] + "; null list: threw " + threw[1] + " (switch " + on + ")";
            bool ok = what == "expect broken" ? !on && threw[0] == "NullReferenceException" && threw[1] == "NullReferenceException"
                                              : on && threw[0] == "nothing" && threw[1] == "nothing";
            if (!ok) throw new InvalidOperationException("rolenullpurge " + what + ": wrong outcome; " + line);
            return "as expected; " + line;
        }
    }
}
