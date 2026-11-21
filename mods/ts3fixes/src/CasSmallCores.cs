namespace Ts3Fixes
{
    // Game-independent rules of castintreset (CasTintReset.cs) and casoccultlog (CasOccultLog.cs), docs/658 §6.4, §6.6.
    // Self-checks: tools/selftest/castintreset, tools/selftest/casoccultlog.
    public static class CasTintResetCore
    {
        // clear the hover pick id left behind when the click-on-Sim panel goes away or turns hover off: only with the
        // switch on and only a non-zero (stale) index; 0 is EA's own reset value
        public static bool ShouldClear(bool on, uint index) { return on && index != 0; }
    }

    public static class CasOccultLogCore
    {
        // AgeChangeWerewolfOutfit returned: it threw (inside EA's catch) when it never reached the outfit save and no
        // wrapped callee already logged the exception
        public static bool WwThrew(bool stored, bool logged) { return !stored && !logged; }
    }
}
