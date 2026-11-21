namespace Ts3Fixes
{
    // Game-independent rules of the docs/661 lot-gameplay fixes. Self-check: tools/selftest/lotgameplay.
    //   - barcloseorphan (BarCloseOrphan.cs): is a bar venue strictly open (after HourOpen, before the staggered exit that
    //     starts kStaggeredExitTimeDelta hours before HourClose)? EA's legit "leave the venue" pushes never run then.
    //   - venueintensity (VenueIntensity.cs): which of MetaAutonomyTuning's two intensity columns EA reads, and whether
    //     the swapped read should be undone.
    //   - visitwalkfinish reason counters (UnseenVisitTrip.cs VwfPass): the reason codes and their counter slots.
    public static class LotGameplayCore
    {
        // EA's SimClock.IsTimeBetweenTimes(between, start, end), the same branches: start < end is a same-day window
        // (both ends inclusive), else it wraps past midnight.
        public static bool Between(float h, float start, float end)
        {
            if (start < end) return h >= start && h <= end;
            return h >= start || h <= end;
        }

        public static float Hour24(float h)
        {
            h %= 24f;
            return h < 0f ? h + 24f : h;
        }

        // True only strictly inside [open, close - stagger): the venue is open and its staggered exit has not begun.
        // EA's BartendingVenueHelper pushes guests out only from close - stagger (RemindVenueNpcGuestsToLeave) and after
        // close until OnOpen removes the alarm (RemindVenueGuestsToLeave), so a push in this window comes from a
        // "remind guests to leave" alarm that OnClose overwrote without removing. Bad hours (open or close < 0, a
        // window shorter than the stagger) answer false: nothing is skipped.
        public static bool StrictlyOpen(float now, float open, float close, float stagger)
        {
            if (open < 0f || close < 0f || stagger < 0f) return false;
            float len = Hour24(close - open);
            if (len <= stagger) return false;
            float end = Hour24(close - stagger);
            float o = Hour24(open), n = Hour24(now);
            if (n == end) return false;
            return Between(n, o, end);
        }

        // MetaAutonomyTuning.GetIntensity reads `IsDowntownWorld() ? Intensity : Intensity_Downtown` (IL_0072-IL_0088):
        // the two columns are swapped. Answer: should the hook swap them for this call? Only when the right column is
        // set (> 0) and differs from what EA reads, so a venue whose Intensity_Downtown row is empty keeps EA's value.
        public static bool ShouldSwap(bool downtown, float intensity, float intensityDowntown)
        {
            float right = downtown ? intensityDowntown : intensity;
            float eaRead = downtown ? intensity : intensityDowntown;
            return right > 0f && right != eaRead;
        }

        // visitwalkfinish pass: why a tracked walker past the 60 sim-minute mark was not interrupted. Slot = kVwfBase + code.
        public const int kVwfBase = 4220;
        public const int kVwfDue = 0, kVwfNotRouting = 1, kVwfResortDive = 2, kVwfPets = 3, kVwfGroup = 4, kVwfLotOther = 5,
            kVwfSeen = 6, kVwfNearViewers = 7, kVwfAskedStill = 8, kVwfDropped = 9;

        public static string VwfName(int code)
        {
            switch (code)
            {
                case kVwfDue: return "due";
                case kVwfNotRouting: return "not_routing";
                case kVwfResortDive: return "resort_dive";
                case kVwfPets: return "pets";
                case kVwfGroup: return "group";
                case kVwfLotOther: return "lot_other";
                case kVwfSeen: return "not_unseen";
                case kVwfNearViewers: return "near_viewers";
                case kVwfAskedStill: return "asked_still_walking";
                case kVwfDropped: return "dropped";
                default: return "?";
            }
        }

        // VwfLotOk's refusals in order, as a reason code (-1 = lot ok). Inputs are the same tests VwfLotOk makes.
        public static int VwfLotReason(bool lotOther, bool resortOrDive, bool pets, bool group)
        {
            if (lotOther) return kVwfLotOther;
            if (resortOrDive) return kVwfResortDive;
            if (pets) return kVwfPets;
            if (group) return kVwfGroup;
            return -1;
        }
    }
}
