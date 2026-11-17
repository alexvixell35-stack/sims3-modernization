using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Abstracts;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Situations;

namespace Ts3Fixes
{
    // weddingcleanup (tuning kWeddingCleanup, default on: rare EA crash fix seen in runs 2026-10-12..11-16). EA bug fix, every
    // Sim; nothing is moved, skipped or faked, EA's CleanUp body still runs.
    //
    // EA's WeddingCeremony.CleanUp (reached via Situation.Exit, e.g. from RouteToWeddingArch.OnRouteFailed when a partner's
    // walk to the arch fails) throws NullReferenceException, ScriptError frame #0 at IL 0x3b / 0x7a, so the rest of the cleanup
    // (the other partner, the audience's roles and watch interactions, base Situation cleanup) never runs [verified: the
    // NRE in probe-out runs 2026-10-12..10-20 with no Ts3Fixes loaded; tools/harness/known-script-errors.json
    // ea-wedding-cleanup-nre]. IL 0x3b [verified, ilspycmd --ilcode on game-copy Sims3GameplaySystems.dll, 1.67] is
    // `callvirt IGameObject::RemoveFromUseList(Sim)` on this.WeddingArch (ldfld 0x30) with this.RingGiver (ldfld 0x36). Its
    // receiver cannot be null there: EA tests WeddingArch at 0x1a (brfalse) and calls IsActorUsingMe on it at 0x28, and the
    // only implementer, WeddingArch : GameObject, inherits GameObject.RemoveFromUseList, whose dereferences are all null-safe
    // (mActorsUsingMe already used by IsActorUsingMe, ScienceComponent ?., SimLine checked). 0x7a in the other variant is an
    // ldarg.0. So the reported offset does not name the null [verified]; which reference is null stays [inferred], and the
    // throw-time line below names it on the next occurrence. Candidates are the references EA does not check: a destroyed
    // ring Sim's or audience Sim's InteractionQueue (Sim.InteractionQueue is null once the Sim is torn down; EA only exits the
    // ceremony on kSelectableSimDied, so an NPC partner that despawned leaves it running) and a destroyed arch's use list.
    // A third-party core mod's IL patches guard the same three spots (docs/data/ilpatch-convert.csv,
    // 99_t1_petpool_wedding: giver/receiver skip destroyed arch, queue null, destroyed audience) [cited, not copied].
    //
    // The guard (WeddingCleanupCore.Plan) acts only where EA would throw: a destroyed arch loses the couple from its use list
    // directly (EA's IsActorUsingMe is then false and its RemoveFromUseList is skipped); a ring or audience Sim whose queue is
    // gone gets EA's RemoveRole and use-list removal here and is hidden from EA's body for the call (the readonly field or
    // the Audience entry is restored afterwards), as there is nothing in its queue to cancel. Everything else is EA's body.
    // Household Sims: a household Sim with a live queue is never hidden; the only visible change is that EA's own cleanup of
    // the ceremony finishes instead of stopping at the throw. Counter [1058] cleanups guarded, [1059] errors or
    // EA's body still threw (then one "weddingcleanup: EA CleanUp threw" line with the state; registry fixes-1058).
    public static partial class Fixes
    {
        static readonly FieldInfo sWcuGiver = typeof(WeddingCeremony).GetField("RingGiver", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo sWcuReceiver = typeof(WeddingCeremony).GetField("RingReceiver", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo sWcuUsers = typeof(GameObject).GetField("mActorsUsingMe", BindingFlags.NonPublic | BindingFlags.Instance);

        static void ApplyWeddingCleanup()
        {
            if (sWcuGiver == null || sWcuReceiver == null || sWcuUsers == null) { AppliedLog().Add("weddingcleanup: WeddingCeremony/GameObject members not found, not patched"); return; }
            Patch(typeof(WeddingCeremony), "CleanUp", typeof(WeddingCleanupHook), "CleanUpHook", "CleanUpOrig");
        }

        public class WeddingCleanupHook
        {
            public void CleanUpHook()
            {
                WeddingCeremony c = (WeddingCeremony)(object)this;
                int plan = 0;
                List<Sim> users = null;
                List<KeyValuePair<int, Sim>> gone = null;
                try { plan = WcuPlan(c, out users, out gone); }
                catch (Exception) { PatchCounters()[1059]++; plan = 0; }
                if (plan == 0) { bool done = false; try { CleanUpOrig(); done = true; } finally { if (!done) WcuThrowLog(c, plan); } return; }
                try
                {
                    if ((plan & WeddingCleanupCore.kArchUseList) != 0) { users.Remove(c.RingGiver); users.Remove(c.RingReceiver); }
                    if ((plan & WeddingCleanupCore.kHideGiver) != 0) WcuDropSim(c, c.RingGiver, users);
                    if ((plan & WeddingCleanupCore.kHideReceiver) != 0) WcuDropSim(c, c.RingReceiver, users);
                    if (gone != null) foreach (KeyValuePair<int, Sim> g in gone) { c.Audience.Remove(g.Value); g.Value.RemoveRole(c); }
                }
                catch (Exception) { PatchCounters()[1059]++; }
                Sim giver = c.RingGiver, receiver = c.RingReceiver;
                try
                {
                    if ((plan & WeddingCleanupCore.kHideGiver) != 0) sWcuGiver.SetValue(c, null);
                    if ((plan & WeddingCleanupCore.kHideReceiver) != 0) sWcuReceiver.SetValue(c, null);
                    PatchCounters()[1058]++;
                    CleanUpOrig();
                    plan = -1;
                }
                finally
                {
                    if (c.RingGiver != giver) sWcuGiver.SetValue(c, giver);
                    if (c.RingReceiver != receiver) sWcuReceiver.SetValue(c, receiver);
                    if (gone != null && c.Audience != null)
                        foreach (KeyValuePair<int, Sim> g in gone) c.Audience.Insert(Math.Min(g.Key, c.Audience.Count), g.Value);
                    if (plan != -1) WcuThrowLog(c, plan);  // after the restore, so the line shows EA's fields
                }
            }
            public void CleanUpOrig() { } // receives WeddingCeremony.CleanUp's original body
        }

        // EA's body threw (no catch: EA's exception and ScriptError stack stay as they are). One line with every reference EA's
        // CleanUp dereferences, so the null at the reported offset is named by the next occurrence (the offset alone cannot:
        // EA null-checks the arch at IL 0x1a and calls it at 0x28 before the 0x3b RemoveFromUseList).
        static void WcuThrowLog(WeddingCeremony c, int plan)
        {
            try
            {
                PatchCounters()[1059]++;
                GameObject arch = c.WeddingArch as GameObject;
                List<Sim> users = arch != null ? sWcuUsers.GetValue(arch) as List<Sim> : null;
                int audGone = 0, audNull = 0;
                if (c.Audience != null) foreach (Sim a in c.Audience) { if (a == null) audNull++; else if (a.InteractionQueue == null) audGone++; }
                AppliedLog().Add("weddingcleanup: EA CleanUp threw: plan=" + plan
                    + " arch=" + (c.WeddingArch == null ? "null" : c.WeddingArch.GetType().Name + (arch != null && arch.HasBeenDestroyed ? " destroyed" : "") + (arch != null && users == null ? " users=null" : ""))
                    + " giver=" + WcuSimState(c.RingGiver, users) + " receiver=" + WcuSimState(c.RingReceiver, users)
                    + " audience=" + (c.Audience == null ? "null" : c.Audience.Count + " noqueue=" + audGone + " null=" + audNull));
            }
            catch (Exception) { }
        }

        static string WcuSimState(Sim s, List<Sim> users)
        {
            if (s == null) return "null";
            return (s.HasBeenDestroyed ? "destroyed" : "live") + (s.InteractionQueue == null ? " queue=null" : "") + (users != null && users.Contains(s) ? " usesArch" : "");
        }

        static bool WcuQueueGone(Sim s) { return s != null && s.InteractionQueue == null; }

        // the guard's plan for this ceremony; users = the destroyed arch's use list (else null), gone = audience Sims with no
        // queue and their indexes (ascending, for the restore)
        static int WcuPlan(WeddingCeremony c, out List<Sim> users, out List<KeyValuePair<int, Sim>> gone)
        {
            users = null; gone = null;
            if (!ShortcutOn("weddingcleanup")) return 0;
            GameObject arch = c.WeddingArch as GameObject;
            if (arch != null) users = sWcuUsers.GetValue(arch) as List<Sim>;
            bool couple = users != null && (users.Contains(c.RingGiver) || users.Contains(c.RingReceiver));
            if (c.Audience != null)
                for (int i = 0; i < c.Audience.Count; i++)
                    if (WcuQueueGone(c.Audience[i])) (gone ?? (gone = new List<KeyValuePair<int, Sim>>())).Add(new KeyValuePair<int, Sim>(i, c.Audience[i]));
            bool destroyed = arch != null && arch.HasBeenDestroyed;
            if (!destroyed) users = null;  // a live arch keeps EA's own RemoveFromUseList
            return WeddingCleanupCore.Plan(true, c.WeddingArch != null, destroyed, couple,
                c.RingGiver != null, WcuQueueGone(c.RingGiver), c.RingReceiver != null, WcuQueueGone(c.RingReceiver), gone != null);
        }

        // EA's part of CleanUp for a ring Sim with no queue: its role and its place in the arch's use list (nothing to cancel)
        static void WcuDropSim(WeddingCeremony c, Sim s, List<Sim> users)
        {
            s.RemoveRole(c);
            if (users != null) users.Remove(s);
            else if (c.WeddingArch != null && c.WeddingArch.IsActorUsingMe(s)) c.WeddingArch.RemoveFromUseList(s);
        }
    }
}
