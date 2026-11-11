using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Ts3Fixes;

// Offline self-check of RestartCore (docs/530 step 1): the handoff record's lines.
static class RestartTest
{
    static int sFails;
    static void Check(bool ok, string what) { if (!ok) { sFails++; Console.WriteLine("FAIL " + what); } }

    static RsRecord Sample()
    {
        RsRecord r = new RsRecord();
        r.Speed = 2; r.Panel = 5; r.Selected = 0xFEDCBA9876543210UL; r.HasCamera = true;
        float[] cam = { 1.5f, -2.25f, 1e-7f, 123456.78f, 0.1f, -0f };
        for (int i = 0; i < 6; i++) r.Cam[i] = cam[i];
        RsSim s = new RsSim();
        s.SimId = 42;
        RsItem a = new RsItem();
        a.Kind = 1; a.Id = 7; a.Level = 3; a.Value = 0.3f; a.Autonomous = false; a.Cancellable = true; a.X = 10.1f; a.Y = 0f; a.Z = -3.3f;
        a.DefType = "Sims3.Gameplay.Objects.Electronics.Computer+UseComputer, Sims3GameplayObjects"; a.DefField = "Singleton";
        a.ObjType = "Sims3.Gameplay.Objects.Electronics.Computer, Sims3GameplayObjects";
        RsItem b = new RsItem();
        b.Kind = 0; b.Id = ulong.MaxValue; b.Level = -1; b.Value = float.MaxValue; b.Autonomous = true; b.Cancellable = false;
        b.DefType = "Sims3.Gameplay.Socializing.SocialInteractionA+Definition, Sims3GameplaySystems"; b.DefField = "sChat";
        s.Items.Add(a); s.Items.Add(b);
        r.Sims.Add(s);
        RsSim empty = new RsSim();
        empty.SimId = 9;
        r.Sims.Add(empty);  // no entries: no S line
        return r;
    }

    static string Join(List<string> l) { return string.Join("\n", l.ToArray()) + "\n"; }

    static void Main()
    {
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");  // a comma decimal culture must not change the text

        int dropped;
        RsRecord r = Sample();
        List<string> lines = RestartCore.Lines(r, out dropped);
        Check(dropped == 0 && lines.Count == 4, "4 lines (R1, S, 2 x I), the empty Sim left out: " + lines.Count);
        Check(lines[0].StartsWith("R1|2|5|18364758544493064720|1|1.5|-2.25|"), "header: " + lines[0]);
        Check(lines[1] == "S|42", "Sim line");
        foreach (string l in lines) Check(l.IndexOf(',') < 0 || l.StartsWith("I|"), "no culture commas outside the type names: " + l);

        RsRecord p = RestartCore.Parse(Join(lines));
        Check(p != null, "parses");
        if (p != null)
        {
            Check(p.Speed == 2 && p.Panel == 5 && p.Selected == r.Selected && p.HasCamera, "header values");
            bool cam = true;
            for (int i = 0; i < 6; i++) cam &= p.Cam[i] == r.Cam[i];
            Check(cam, "camera floats round-trip exactly");
            Check(p.Sims.Count == 1 && p.Sims[0].SimId == 42 && p.Sims[0].Items.Count == 2, "one Sim, two entries");
            RsItem a = p.Sims[0].Items[0], b = p.Sims[0].Items[1];
            Check(a.Kind == 1 && a.Id == 7 && a.Level == 3 && a.Value == 0.3f && !a.Autonomous && a.Cancellable && a.X == 10.1f && a.Z == -3.3f, "object entry");
            Check(a.DefType == r.Sims[0].Items[0].DefType && a.DefField == "Singleton" && a.ObjType == r.Sims[0].Items[0].ObjType, "object entry names");
            Check(b.Kind == 0 && b.Id == ulong.MaxValue && b.Level == -1 && b.Value == float.MaxValue && b.Autonomous && !b.Cancellable && b.ObjType == "", "Sim entry");
        }

        // refusals: any bad line and nothing is restored
        string good = Join(lines);
        Check(RestartCore.Parse(null) == null && RestartCore.Parse("") == null, "empty");
        Check(RestartCore.Parse(good.Replace("R1|", "R2|")) == null, "another schema");
        Check(RestartCore.Parse(good.Substring(good.IndexOf('\n') + 1)) == null, "no header");
        Check(RestartCore.Parse(good.Replace("S|42", "S|-42")) == null, "negative id");
        Check(RestartCore.Parse(good.Replace("S|42", "S|42|x")) == null, "Sim line with an extra field");
        Check(RestartCore.Parse(good.Replace("|Singleton|", "||")) == null, "no definition field");
        Check(RestartCore.Parse(good.Replace("I|1|7|", "I|3|7|")) == null, "unknown kind");
        Check(RestartCore.Parse(good.Replace("I|1|7|", "I|0|7|")) == null, "Sim kind with an object type");
        Check(RestartCore.Parse(good.Replace("|1.5|", "|1,5|")) == null, "comma decimal");
        Check(RestartCore.Parse(good + "X|1\n") == null, "unknown line");
        Check(RestartCore.Parse("R1|2|5|0|1|0|0|0|0|0|0\nI|0|1|0|0|0|0|0|0|0|T, A|f|\n") == null, "entry before any Sim");
        string cut = good.Substring(0, good.Length - 20);
        Check(RestartCore.Parse(cut) == null, "truncated in an entry");
        Check(RestartCore.Parse(good.Replace("|1|1.5|", "|2|1.5|")) == null, "camera flag not 0/1");
        Check(RestartCore.Parse("R1|2|5|0|0|0|0|0|0|0|0\n") != null, "header alone is a record (nothing queued)");

        // limits: a line over 460 characters is dropped, the total stays under 60000
        RsRecord big = new RsRecord();
        for (int i = 0; i < 400; i++)
        {
            RsSim s = new RsSim();
            s.SimId = (ulong)i;
            for (int k = 0; k < 8; k++)
            {
                RsItem it = new RsItem();
                it.DefType = new string('T', k == 0 && i == 0 ? 500 : 60) + ", Asm";
                it.DefField = "Singleton";
                s.Items.Add(it);
            }
            big.Sims.Add(s);
        }
        List<string> bl = RestartCore.Lines(big, out dropped);
        int total = 0;
        foreach (string l in bl) { total += l.Length + 1; Check(l.Length <= RestartCore.kLineMax, "line limit"); }
        Check(total <= RestartCore.kTotalMax && dropped > 1 && RestartCore.Parse(Join(bl)) != null, "total limit, still a record: " + total + ", dropped " + dropped);
        RsRecord odd = Sample();
        odd.Sims[0].Items[0].DefType = "Caf\u00e9, Asm";
        RestartCore.Lines(odd, out dropped);
        Check(dropped == 1, "non-ASCII type name dropped");

        // save name, bits
        Check(RestartCore.SameSave("My Save.sims3", "my save.SIMS3"), "same save, any case");
        Check(RestartCore.SameSave("C:\\x\\Saves\\My Save.sims3", "My Save.sims3") && RestartCore.SameSave("a/My Save.sims3", "My Save.sims3"), "a path in LoadFileName");
        Check(!RestartCore.SameSave("Other.sims3", "My Save.sims3") && !RestartCore.SameSave(null, "x") && !RestartCore.SameSave("x", null), "other save");
        bool[] bits = new bool[32];
        bits[0] = bits[31] = true;
        Check(RestartCore.FromBits(bits) == 0x80000001u, "32 bits");

        Console.WriteLine(sFails == 0 ? "restart selftest: ok" : "restart selftest: " + sFails + " failures");
        Environment.Exit(sFails == 0 ? 0 : 1);
    }
}
