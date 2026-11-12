using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.DreamsAndPromises;
using Sims3.Gameplay.EventSystem;

namespace Ts3Fixes
{
    // dreamrootorphan + blogorphan + dnpupdateguard (tunings kDreamRootOrphan, kBlogOrphan, kDnpUpdateGuard; default off
    // until measured; docs/191 "event listeners").
    //
    // dreamrootorphan, EA bug [verified decompiled]: DreamTree.ActivateTreeForSim creates a tree's root node with its event
    // listener on (Root.CreateNode(s, null, activateListener: true)), then drops the node when Test() says kThrowAway or
    // kNotSupported, without removing the listener. That listener stays registered for the session (it is not saved), and
    // when its trigger event fires it can complete the discarded root (DefaultFeedbackFunction returns true) and score the
    // tree's children. The pass: after ActiveDreamNode.Test returns kThrowAway / kNotSupported for a node that is not in its
    // owner's wish manager (ActiveNodes), the node's listener is removed (RemoveListenerFromTracker). Live wishes are always
    // in the manager (RetestNode tests displayed/promised ones; TryAddLifetimeWish adds before testing), so they are not
    // touched; nodes created without a listener (ScoreChildren) have none to remove.
    //
    // blogorphan, EA bug [verified decompiled]: SocialNetworkingSkill's blog listener creates a one-off dream per tested
    // topic with DreamsAndPromisesManager.CreateOneOffDream (listener on) only to call RunStandaloneFeedbackFunction on it,
    // and never removes it: one leftover listener per topic per event. The pass: after RunStandaloneFeedbackFunction, a
    // one-off node that is not in its owner's manager loses its listener (AddOneOffDream nodes are in the manager: kept).
    //
    // dnpupdateguard, crash guard [verified decompiled, EventTracker.SendEvent 160-165]: the wish drain calls
    // sNeedToUpdateList[0].Update() and removes it only afterwards; when Update throws, that manager stays first in the list,
    // every later SendEvent retries it and the exception goes to whatever code sent the event (and LifeEventManager /
    // challenge checks are skipped). The pass: an exception from DreamsAndPromisesManager.Update is counted and not
    // rethrown, so the drain removes the manager and goes on. Nothing changes unless Update throws.
    // Counters: [2260] root listeners left by a thrown-away test (also counted off), [2261] removed, [2262] errors,
    // [2263] blog one-off listeners left (also counted off), [2264] removed, [2265] errors, [2266] Update exceptions seen,
    // [2267] swallowed, [2268] errors.
    public static partial class Fixes
    {
        static FieldInfo sWlListener;

        static void ApplyWishListenerFixes()
        {
            sWlListener = typeof(ActiveDreamNode).GetField("mEventListener", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (sWlListener == null) { AppliedLog().Add("dreamrootorphan/blogorphan: mEventListener not found, not patched"); }
            else
            {
                Patch(typeof(ActiveDreamNode), "Test", Type.EmptyTypes, typeof(WlNode), "TestHook", "TestOrig");
                Patch(typeof(ActiveDreamNode), "RunStandaloneFeedbackFunction", new Type[] { typeof(Event) }, typeof(WlNode), "StandaloneHook", "StandaloneOrig");
            }
            Patch(typeof(DreamsAndPromisesManager), "Update", Type.EmptyTypes, typeof(WlMgr), "UpdateHook", "UpdateOrig");
        }

        static bool WlInManager(ActiveDreamNode n)
        {
            Sim o = n.Owner;
            DreamsAndPromisesManager m = o == null ? null : o.DreamsAndPromisesManager;
            if (m == null || m.ActiveNodes == null) return false;
            foreach (ActiveNodeBase a in m.ActiveNodes) if (object.ReferenceEquals(a, n)) return true;
            return false;
        }

        public class WlNode
        {
            public ActiveNodeBase.CheckResult TestHook()
            {
                ActiveNodeBase.CheckResult r = TestOrig();
                if (r != ActiveNodeBase.CheckResult.kThrowAway && r != ActiveNodeBase.CheckResult.kNotSupported) return r;
                try
                {
                    ActiveDreamNode n = (ActiveDreamNode)(object)this;
                    if (sWlListener.GetValue(n) == null || WlInManager(n)) return r;
                    int[] c = PatchCounters();
                    c[2260]++;
                    if (ShortcutOn("dreamrootorphan")) { n.RemoveListenerFromTracker(); c[2261]++; }
                }
                catch (Exception) { PatchCounters()[2262]++; }
                return r;
            }
            public ActiveNodeBase.CheckResult TestOrig() { return ActiveNodeBase.CheckResult.kThrowAway; }

            public bool StandaloneHook(Event e)
            {
                bool r = StandaloneOrig(e);
                try
                {
                    ActiveDreamNode n = (ActiveDreamNode)(object)this;
                    if (!n.IsOneOffNode || sWlListener.GetValue(n) == null || WlInManager(n)) return r;
                    int[] c = PatchCounters();
                    c[2263]++;
                    if (ShortcutOn("blogorphan")) { n.RemoveListenerFromTracker(); c[2264]++; }
                }
                catch (Exception) { PatchCounters()[2265]++; }
                return r;
            }
            public bool StandaloneOrig(Event e) { return false; }
        }

        public class WlMgr
        {
            public void UpdateHook()
            {
                if (!ShortcutOn("dnpupdateguard")) { UpdateOrig(); return; }
                try { UpdateOrig(); }
                catch (Exception)
                {
                    int[] c = PatchCounters();
                    c[2266]++; c[2267]++;
                }
            }
            public void UpdateOrig() { }
        }
    }
}
