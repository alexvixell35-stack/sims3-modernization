using System;
using System.Collections.Generic;
using Ts3Agent;

// args: none = self-check; a map file = print the plan (layout-map.ts).
static class LayoutMapTest
{
    static readonly string[] kExample = { "W w w w", "E X w s", "E E w e", "E e w e", "E b w e" };

    static Dictionary<char, int> Items() { Dictionary<char, int> d = new Dictionary<char, int>(); d['s'] = -1; d['b'] = -1; return d; }

    static void Is(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }

    static void Bad(string[] map, string part)
    {
        try { LayoutMap.Parse(map, Items()); }
        catch (ArgumentException e)
        {
            Is(e.Message.IndexOf(part) >= 0, "error '" + e.Message + "' should say '" + part + "'");
            return;
        }
        throw new Exception("FAIL: no error for " + string.Join("/", map));
    }

    static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            Console.WriteLine(LayoutMap.Parse(System.IO.File.ReadAllLines(args[0]), Items()).Describe());
            return 0;
        }
        // the example: wall column 2 and wall row 0 collapse; X and s sit on opposite faces of the wall line x = 2
        LayoutPlan p = LayoutMap.Parse(kExample, Items());
        Is(p.W == 3 && p.D == 4, "size " + p.W + "x" + p.D);
        Is(p.Edges.Count == 7, "7 edges, got " + p.Edges.Count);
        Is(p.Runs.Count == 2, "2 runs, got " + p.Runs.Count);
        Is(p.Runs[0][0] == 2 && p.Runs[0][1] == 0 && p.Runs[0][2] == 2 && p.Runs[0][3] == 4, "vertical run x=2 z 0..4");
        Is(p.Runs[1][0] == 0 && p.Runs[1][1] == 0 && p.Runs[1][2] == 3 && p.Runs[1][3] == 0, "horizontal run z=0 x 0..3");
        Is(p.SimX == 1 && p.SimZ == 0, "X on game tile 1,0");
        Is(p.Items.Count == 2, "2 items");
        Is(p.Items[0].Ch == 's' && p.Items[0].X == 2 && p.Items[0].Z == 0, "stove tile 2,0");
        Is(p.WallOn(1, 0, 1) && p.WallOn(2, 0, 3), "one wall between X and the stove");
        Is(p.Items[0].Dir == 1, "stove back on the wall shared with X: faces E, got " + p.Items[0].Dir);
        Is(p.Items[1].Ch == 'b' && p.Items[1].X == 1 && p.Items[1].Z == 3 && p.Items[1].Dir == 3, "bin tile 1,3 back on the x=2 wall, faces W");

        // a gap: an empty tile in a wall line is a hole of one edge, two runs
        LayoutPlan g = LayoutMap.Parse(new string[] { "e w e", "e w e", "e e e", "e w e", "e w e" }, Items());
        Is(g.W == 2 && g.D == 5 && g.Edges.Count == 4 && g.Runs.Count == 2, "gap: 2 runs");
        Is(!g.WallOn(0, 2, 1), "gap: no wall at row 2");

        // boundary wall: the line lies on the footprint's edge
        LayoutPlan b = LayoutMap.Parse(new string[] { "w e", "w e" }, Items());
        Is(b.W == 1 && b.D == 2 && b.Runs.Count == 1 && b.Runs[0][0] == 0 && b.Runs[0][2] == 0, "wall on x=0");

        // legend facing beats auto; no wall: S
        Dictionary<char, int> leg = Items();
        leg['f'] = 0;
        LayoutPlan f = LayoutMap.Parse(new string[] { "f s" }, leg);
        Is(f.Items[0].Dir == 0 && f.Items[1].Dir == 2, "legend facing, default S");

        // wrong maps fail with a reason, never a guess
        Bad(new string[] { "e w e", "e e e" }, "no run of 2");
        Bad(new string[] { "w e", "w e", "s e" }, "column 0");
        Bad(new string[] { "w w s", "e e e" }, "row 0");
        Bad(new string[] { "w w", "e X", "X e" }, "more than one X");
        Bad(new string[] { "e e", "e" }, "differ in length");
        Bad(new string[] { "e q" }, "unknown map char 'q'");
        Bad(new string[] { "" }, "empty map");
        Bad(new string[] { "w w", "w w" }, "no tiles left");

        // rotation keeps sizes, and tile corners stay inside the rotated bounds
        foreach (int rot in new int[] { 0, 90, 180, 270 })
        {
            int[] box = LayoutMap.Bounds(3, 4, rot);
            Is((box[2] - box[0]) * (box[3] - box[1]) == 12, "bounds at " + rot);
            for (int x = 0; x < 3; x++)
                for (int z = 0; z < 4; z++)
                {
                    int[] t = LayoutMap.Tile(x, z, rot);
                    Is(t[0] >= box[0] && t[0] < box[2] && t[1] >= box[1] && t[1] < box[3], "tile in bounds at " + rot);
                }
            for (int d = 0; d < 4; d++)
            {
                int[] v = LayoutMap.DirOnLot(d, rot), w = LayoutMap.DirOnLot((d + 2) % 4, rot);
                Is(v[0] == -w[0] && v[1] == -w[1], "opposite directions at " + rot);
            }
            // the tile east of (1,0) on the map is the neighbour in DirOnLot(E)
            int[] a = LayoutMap.Tile(1, 0, rot), c = LayoutMap.Tile(2, 0, rot), e = LayoutMap.DirOnLot(1, rot);
            Is(c[0] - a[0] == e[0] && c[1] - a[1] == e[1], "tile step matches direction at " + rot);
        }
        int[] v90 = LayoutMap.Vertex(1, 0, 90);
        Is(v90[0] == 0 && v90[1] == 1, "rotation 90: col + runs along lot +z");
        try { LayoutMap.Bounds(1, 1, 45); throw new Exception("FAIL: rotation 45 accepted"); }
        catch (ArgumentException) { }
        Console.WriteLine("layout-map self-check ok");
        return 0;
    }
}
