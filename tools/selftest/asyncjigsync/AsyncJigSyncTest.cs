// Offline self-check of asyncjigsync (mods/ts3fixes/src/AsyncJigSyncCore.cs, docs/240): fake interactions with jig fields
// (public on a base class, private on the subclass, typed as a jig subclass) and a fake InWorld flag.
using System;
using Ts3Fixes;

static class AsyncJigSyncTest
{
    static int fails;
    static void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL " + what); } }

    class Jig { public bool InWorld; }
    class JigTwo : Jig { }
    class SocialBase { public Jig SocialJig; }                 // SocialInteraction.SocialJig
    class ShowOff : SocialBase { private JigTwo SimJig; public void Set(JigTwo j) { SimJig = j; } }
    class NoJig { public string X = ""; }

    static readonly Predicate<object> Unplaced = delegate(object j) { return !((Jig)j).InWorld; };

    static int Main()
    {
        Check(AsyncJigSyncCore.Fields(typeof(ShowOff), typeof(Jig)).Length == 2, "base public + own private jig fields found");
        Check(AsyncJigSyncCore.Fields(typeof(NoJig), typeof(Jig)).Length == 0, "no jig field");
        Check(!AsyncJigSyncCore.Holds(null, typeof(Jig), Unplaced), "null interaction");
        ShowOff s = new ShowOff();
        Check(!AsyncJigSyncCore.Holds(s, typeof(Jig), Unplaced), "no jig yet");
        JigTwo j = new JigTwo();
        s.Set(j);
        Check(AsyncJigSyncCore.Holds(s, typeof(Jig), Unplaced), "private jig out of world = pending");
        j.InWorld = true;
        Check(!AsyncJigSyncCore.Holds(s, typeof(Jig), Unplaced), "jig in world = not pending");
        s.SocialJig = new Jig();
        Check(AsyncJigSyncCore.Holds(s, typeof(Jig), Unplaced), "base-class jig out of world = pending");
        Check(!AsyncJigSyncCore.Holds(new NoJig(), typeof(Jig), Unplaced), "type without jig fields");
        Console.WriteLine(fails == 0 ? "asyncjigsync selftest: ok" : "asyncjigsync selftest: " + fails + " failed");
        return fails == 0 ? 0 : 1;
    }
}
