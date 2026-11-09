using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.CAS;
using Sims3.Gameplay.Core;
using Sims3.Gameplay.Interfaces;

namespace Ts3Fixes
{
    // simrefsdeadsim (issue #40; errortrapsims' hourly alarm, own switch; not save data): three references from a live Sim
    // to another Sim that EA never drops when that other Sim object is destroyed, so each destroyed Sim stays alive (with
    // its components, queue and every dead Sim it points at in turn) as long as the live Sim does. al343 (story progression
    // spam, 45 min): 23 of 40 sampled dead Sim objects sat in one cluster reached through these three:
    // - SocialComponent.mLastTalkingTo: set by TalkingTo for every conversation. SocialComponent.Dispose clears the other
    //   Sim's field only when the two point at each other, so a Sim that last talked to someone who then talked to a
    //   third Sim keeps the first one after it is destroyed. Set to null (the "no one" value EA starts with and the HUD
    //   checks for).
    // - SimRoutingComponent.mPusherSim: the Sim that asked for a mid-route push, never cleared. Set to null only when no
    //   push is pending or running (mbPushRequested / mbCurrentlyInPush false), since a pending push plans with it.
    // - Celebrity.mWaitingToReact (Sim.mCelebrity): Sims that will react to the celebrity; a reactor removes itself when
    //   its reaction ends, which a destroyed one never does. Destroyed reactors removed.
    // - Occult*.mOwningSim (OccultManager.mOccultList; a private field on each occult class that has one): set by
    //   FixUpOccult when the Sim is instantiated (OccultManager.SetupSimHandlers / SetupForInstantiatedSim), never cleared
    //   when it is destroyed, so a townie's description keeps its last Sim object (al357: werewolf and vampire paths). Set
    //   to the description's current Sim, or null when it has none: the value EA leaves for occult townies that are not in
    //   the world (FixUpOccult runs only on instantiation; Vampire's dereferences the Sim, so EA never calls it without one).
    //   The active household is skipped.
    // Household rule: household Sims, the selected Sim and Sims of unknown scope (HhScope) are skipped in the actor
    // loop; the occult pass skips the active and played households and the selected Sim; with no active household
    // nothing runs.
    // "Gone" is errortrapsims' rule (destroyed, or its description now names another Sim object). Counters: [2084] last
    // talking-to cleared, [2085] pushers cleared, [2086] reactors removed, [2087] occult owners reset.
    public static partial class Fixes
    {
        static FieldInfo sSrPusher, sSrPushReq, sSrInPush, sSrCeleb, sSrWaiting, sSrOccults;
        static readonly Dictionary<Type, FieldInfo> sSrOwner = new Dictionary<Type, FieldInfo>();
        static bool sSrTried, sSrThrew;

        static void SimRefsDeadSimPass()
        {
            try
            {
                if (!ShortcutOn("simrefsdeadsim") || LotManager.Actors == null) return;
                if (!sSrTried)
                {
                    sSrTried = true;
                    Type rc = typeof(SimRoutingComponent), cel = typeof(Sims3.Gameplay.Careers.Celebrity);
                    sSrPusher = rc.GetField("mPusherSim", kInstAll);
                    sSrPushReq = rc.GetField("mbPushRequested", kInstAll);
                    sSrInPush = rc.GetField("mbCurrentlyInPush", kInstAll);
                    sSrCeleb = typeof(Sim).GetField("mCelebrity", kInstAll);
                    sSrWaiting = cel.GetField("mWaitingToReact", kInstAll);
                    sSrOccults = typeof(Sims3.Gameplay.ActorSystems.OccultManager).GetField("mOccultList", kInstAll);
                    AppliedLog().Add("simrefsdeadsim: " + (sSrPusher != null && sSrPushReq != null && sSrInPush != null ? "pusher ok" : "pusher fields differ, off")
                        + ", " + (sSrCeleb != null && sSrWaiting != null ? "celebrity ok" : "celebrity fields differ, off")
                        + ", " + (sSrOccults != null ? "occults ok" : "occult list differs, off"));
                }
                // household rule (CLAUDE.md): no active household = unknown scope for every Sim, so the whole pass waits
                Household act = Household.ActiveHousehold;
                if (act == null) return;
                IActor sel = PlumbBob.SelectedActor;
                int[] c = PatchCounters();
                foreach (Sim s in new List<Sim>(LotManager.Actors))
                {
                    if (s == null || s.HasBeenDestroyed || HhScope(s) || s == sel) continue;
                    Sims3.Gameplay.Socializing.SocialComponent sc = s.SocialComponent;
                    if (sc != null && sc.LastTalkingTo != null && EtSimGone(sc.LastTalkingTo)) { sc.LastTalkingTo = null; c[2084]++; }
                    SimRoutingComponent r = s.SimRoutingComponent;
                    if (r != null && sSrPusher != null && sSrPushReq != null && sSrInPush != null)
                    {
                        Sim p = sSrPusher.GetValue(r) as Sim;
                        if (p != null && EtSimGone(p) && !(bool)sSrPushReq.GetValue(r) && !(bool)sSrInPush.GetValue(r)) { sSrPusher.SetValue(r, null); c[2085]++; }
                    }
                    if (sSrCeleb != null && sSrWaiting != null)
                    {
                        object cel = sSrCeleb.GetValue(s);
                        List<Sim> w = cel != null ? sSrWaiting.GetValue(cel) as List<Sim> : null;
                        if (w != null && w.Count > 0) c[2086] += w.RemoveAll(delegate (Sim x) { return x == null || EtSimGone(x); });
                    }
                }
                if (sSrOccults == null) return;
                foreach (SimDescription sd in Household.EverySimDescription())
                {
                    // player households (active or played before) skipped, and the selected Sim's description
                    if (sd == null || sd.OccultManager == null || sd.Household == act || HouseholdPlayed(sd.Household)
                        || (sel != null && sd.CreatedSim == sel)) continue;
                    List<Sims3.Gameplay.ActorSystems.OccultBaseClass> occ = sSrOccults.GetValue(sd.OccultManager) as List<Sims3.Gameplay.ActorSystems.OccultBaseClass>;
                    if (occ == null) continue;
                    foreach (Sims3.Gameplay.ActorSystems.OccultBaseClass o in occ)
                    {
                        if (o == null) continue;
                        FieldInfo f;
                        Type t = o.GetType();
                        if (!sSrOwner.TryGetValue(t, out f)) { f = t.GetField("mOwningSim", kInstAll); if (f != null && f.FieldType != typeof(Sim)) f = null; sSrOwner[t] = f; }
                        Sim ow = f != null ? f.GetValue(o) as Sim : null;
                        if (ow == null || !EtSimGone(ow)) continue;
                        Sim cur = sd.CreatedSim;
                        f.SetValue(o, cur != null && !SimGone(cur) ? cur : null);
                        c[2087]++;
                    }
                }
            }
            catch (Exception e)
            {
                if (!sSrThrew) { sSrThrew = true; AppliedLog().Add("simrefsdeadsim: pass threw " + e.Message); }
            }
        }
    }
}
