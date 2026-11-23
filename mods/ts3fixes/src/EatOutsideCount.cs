using System;
using System.Collections.Generic;
using System.Reflection;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Objects.RabbitHoles;

namespace Ts3Fixes
{
    // eatoutsidecount (tuning kEatOutsideCount, off by default; docs/569 §2). EA bug fix in Bistro "Eat Outside" with
    // others (EatOutside / EatOutsideWith, both internal).
    //
    // [verified, decompiled] The leader's EatOutside.Run waits while mNumFollowersLeft > 0, then ends every follower's
    // EatOutsideWith (Finished) and seats each Sim that holds food. A follower calls NotifyFollowerDone (count - 1)
    // when it comes out of the bistro with food, then idles in DoLoop. If it is then cancelled or pushed off (anything
    // but the leader's Finished), its Cleanup calls NotifyFollowerCanceled, which removes it and decrements the count
    // a second time. One follower can so release the leader while another is still inside: that one gets Finished on
    // its way out, is not holding food when the leader seats the group, so it is never seated or charged and walks
    // around with its plate.
    // Fix: NotifyFollowerDone notes the (meal, Sim); NotifyFollowerCanceled for a noted Sim runs EA's body (removes it
    // from the group) and then puts back the second decrement, so each follower counts once. Everything else is EA's.
    //
    // Counters: [2568] double decrements undone, [2569] errors. Check: EatOutsideCountChecks.cs.
    public static partial class Fixes
    {
        static readonly Type sEocType = typeof(Bistro).Assembly.GetType("Sims3.Gameplay.Objects.RabbitHoles.EatOutside");
        static readonly FieldInfo sEocLeft = sEocType == null ? null : sEocType.GetField("mNumFollowersLeft", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly FieldInfo sEocFollowers = sEocType == null ? null : sEocType.GetField("Followers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        static readonly List<KeyValuePair<object, Sim>> sEocDone = new List<KeyValuePair<object, Sim>>();

        static void ApplyEatOutsideCount()
        {
            if (sEocType == null || sEocLeft == null || sEocFollowers == null)
            {
                AppliedLog().Add("eatoutsidecount: Bistro EatOutside members not found, not patched");
                return;
            }
            Patch(sEocType, "NotifyFollowerDone", typeof(EocHook), "DoneHook", "DoneOrig");
            Patch(sEocType, "NotifyFollowerCanceled", typeof(EocHook), "CanceledHook", "CanceledOrig");
        }

        // ---- decision (also used by the check): restore the count after EA's cancel notice? ----
        internal static bool EocRestore(bool on, bool alreadyDone, bool wasInGroup)
        {
            return on && alreadyDone && wasInGroup;
        }

        static int EocFind(object meal, Sim sim)
        {
            for (int i = 0; i < sEocDone.Count; i++)
                if (sEocDone[i].Key == meal && sEocDone[i].Value == sim) return i;
            return -1;
        }

        public class EocHook
        {
            public void DoneHook(Sim sim)
            {
                DoneOrig(sim);
                if (!ShortcutOn("eatoutsidecount")) return;
                try
                {
                    object st = this;
                    List<Sim> group = sEocFollowers.GetValue(st) as List<Sim>;
                    if (group == null || !group.Contains(sim) || EocFind(st, sim) >= 0) return;
                    if (sEocDone.Count > 256) sEocDone.RemoveRange(0, 128);
                    sEocDone.Add(new KeyValuePair<object, Sim>(st, sim));
                }
                catch (Exception) { PatchCounters()[2569]++; }
            }
            public void DoneOrig(Sim sim) { } // receives EatOutside.NotifyFollowerDone's original body

            public void CanceledHook(Sim sim)
            {
                object st = this;
                int noted = -1;
                bool inGroup = false;
                try
                {
                    noted = EocFind(st, sim);
                    List<Sim> group = sEocFollowers.GetValue(st) as List<Sim>;
                    inGroup = group != null && group.Contains(sim);
                }
                catch (Exception) { PatchCounters()[2569]++; }
                CanceledOrig(sim);
                if (noted >= 0) sEocDone.RemoveAt(noted);
                if (!EocRestore(ShortcutOn("eatoutsidecount"), noted >= 0, inGroup)) return;
                try
                {
                    sEocLeft.SetValue(st, (int)sEocLeft.GetValue(st) + 1);
                    PatchCounters()[2568]++;
                }
                catch (Exception) { PatchCounters()[2569]++; }
            }
            public void CanceledOrig(Sim sim) { } // receives EatOutside.NotifyFollowerCanceled's original body
        }
    }
}
