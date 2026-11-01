using System;

namespace Ts3Fixes
{
    // Game-independent decision of buffupdate (BuffUpdate.cs, issue #166 build 20, docs/510). Self-check: tools/selftest/buffupdate.
    public static class BuffUpdateCore
    {
        // Whether an exception is a collection walk that saw its collection change: the game's Mono says "out of sync" for a
        // dictionary's enumerators, .NET and newer Mono say "Collection was modified". Other exceptions are not ours to catch.
        public static bool OutOfSync(Exception e)
        {
            InvalidOperationException io = e as InvalidOperationException;
            if (io == null || io.Message == null) return false;
            return io.Message.IndexOf("out of sync", StringComparison.Ordinal) >= 0
                || io.Message.IndexOf("Collection was modified", StringComparison.Ordinal) >= 0;
        }
    }
}
