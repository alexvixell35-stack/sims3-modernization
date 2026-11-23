namespace Ts3Fixes
{
    // Game-independent decision of burglarapt (BurglarApt.cs, docs/866), kept free of game types so the offline
    // self-test (tools/selftest/burglarapt) compiles it alone.
    public static class BaCore
    {
        // True when the hook turns EA's "yes" of BurglarSituation.Stealing.CanBeStolen into "no": an object EA accepts on an
        // apartment lot (Lot.HasVirtualResidentialSlots) that sits in a hidden neighbour unit or a public room, the rule
        // EA's own GameObject.CanBeRepossessed applies [verified, decompiled].
        public static bool Refuse(bool eaAccepts, bool aptLot, bool hiddenRoom, bool publicRoom)
        {
            return eaAccepts && aptLot && (hiddenRoom || publicRoom);
        }

        // The hook's answer: EA's own with the switch off.
        public static bool Answer(bool on, bool eaAccepts, bool aptLot, bool hiddenRoom, bool publicRoom)
        {
            return eaAccepts && !(on && Refuse(eaAccepts, aptLot, hiddenRoom, publicRoom));
        }
    }
}
