using System;
using Sims3.Gameplay.Actors;
using Sims3.Gameplay.Situations;
using Sims3.SimIFace;

namespace Ts3Fixes
{
    // privacyroom (default off): autonomy stops choosing objects inside a bathroom (shower, toilet, bathtub, WooHoo) that
    // another Sim is using with privacy. EA bug [verified, decompiled]: GenericPrivacySituation.SimIsInPrivacySituation
    // returns true for OtherSims too, and the situation's first intruder check (about a sim-minute in) adds every other Sim
    // on the lot to OtherSims. PrivacySituation.OnAllowedInRoomCheck then lets all of them "into" the room, so autonomy picks
    // the toilet or sink in there; but OnPortalPathPlan still locks the door for their autonomous routes
    // (!OtherSimCanIgnorePrivacy), so the Sim walks to the door and the route fails. That failure marks the object blocked for every
    // Sim (GameObject.UpdateBlockTime: autonomous tests skip it for 1 sim-minute, then its score climbs back over 30,
    // docs/04a) and a household Sim then idles 5 sim-minutes (idlegap). With the
    // switch on, only participants count as in the situation: the same answer EA gives in the first minute, before
    // OtherSims fills, and the same rule the door applies. The shower, toilet, bathtub and WooHoo situations do not
    // override the method, so this one hook covers them. User-directed routes are not affected (the door lets them through).
    public static partial class Fixes
    {
        static bool sPrOn;
        static int sPrPoll, sPrChanged, sPrSeenOff;
        const int kPrLogMax = 200;

        static void ApplyPrivacyRoom()
        {
            Patch(typeof(GenericPrivacySituation), "SimIsInPrivacySituation", new Type[] { typeof(Sim) }, typeof(PrivacyRoomHook), "InHook", "InOrig");
        }

        // the switch, re-read every 256 calls: autonomy asks this for every object it tests in the room
        static bool PrOn()
        {
            if ((++sPrPoll & 255) == 1) sPrOn = ShortcutOn("privacyroom");
            return sPrOn;
        }

        public class PrivacyRoomHook
        {
            // EA's body folded in (Participants or OtherSims), so the hook adds no interpreter frame: it runs inside route
            // planning's portal checks (issue #92, the 63-frame limit). InOrig is only the fallback after an error.
            public bool InHook(Sim actor)
            {
                try
                {
                    GenericPrivacySituation g = (GenericPrivacySituation)(object)this;
                    if (g.Participants.Contains(actor)) return true;
                    if (!g.OtherSims.Contains(actor)) return false;
                    if (!PrOn())
                    {
                        // switch off: the same answers, logged as EA's, so an off run shows the case the fix would change
                        if (sPrSeenOff < kPrLogMax)
                            DiagLine("privacyroom: off: " + actor.FullName + " let into " + g.GetType().Name + " (" + (g.Participants.Count > 0 && g.Participants[0] != null ? g.Participants[0].FullName : "?")
                                + ") by EA, seen " + (++sPrSeenOff));
                        return true;  // EA's answer
                    }
                    // the first kPrLogMax answers that differ from EA's: shows the fix firing and for which rooms
                    if (sPrChanged < kPrLogMax)
                        DiagLine("privacyroom: " + actor.FullName + " kept out of " + g.GetType().Name + " (" + (g.Participants.Count > 0 && g.Participants[0] != null ? g.Participants[0].FullName : "?")
                            + "), EA would let in, change " + (++sPrChanged));
                    return false;
                }
                catch (ResetException) { throw; }
                catch (Exception) { }
                return InOrig(actor);
            }
            public bool InOrig(Sim actor) { return false; }  // receives GenericPrivacySituation.SimIsInPrivacySituation's body
        }
    }
}
