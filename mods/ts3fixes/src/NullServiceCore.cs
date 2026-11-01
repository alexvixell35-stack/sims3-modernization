using System.Collections.Generic;

namespace Ts3Fixes
{
    // Game-independent part of nullservice (NullService.cs, docs/504). Self-check: tools/selftest/nullservice.
    public static class NullServiceCore
    {
        // Removes every null element in place, with EA's own idiom from GameObject.Init (`while (list.Remove(null)) {}`), and
        // returns how many it removed. With no null, List.Remove changes nothing (not even the list version), so a caller that
        // is enumerating the list is disturbed only when a null is there, and that caller would have dereferenced it.
        public static int DropNulls<T>(List<T> list) where T : class
        {
            if (list == null) return 0;
            int n = 0;
            while (list.Remove(null)) n++;
            return n;
        }
    }
}
