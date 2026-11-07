using System;
using System.Collections.Generic;
using System.Text;

namespace Ts3Agent
{
    // Game-independent parser behind build_layout (tools/harness/layout-map.ts --self-check checks it).
    //
    // A text map has one char per tile; in the game walls run on tile edges and take no tile, and a Sim stands on a tile.
    // So a map is not literal geometry. Reading:
    //   - A wall line is a column (or row) with a straight run of 2+ w tiles whose other tiles are all empty.
    //     It collapses: the column or row takes no game tile, and its w tiles become unit wall edges on the grid line
    //     between its two neighbour columns (rows). An empty tile in a wall line is a gap in the wall (a door hole).
    //   - All other columns and rows stay and are numbered 0.. without the collapsed ones: game size W x D.
    //   - A w tile on no wall line (run of 1), or an object or X on a wall line, is an error, never guessed.
    // So in "E X w s" X and s end up on tiles next to each other, on opposite faces of one thin wall.
    // Vertices: (x, z) with x 0..W, z 0..D; tile (x, z) has corners (x, z) .. (x + 1, z + 1).
    // Map directions: 0 N (row -), 1 E (col +), 2 S (row +), 3 W (col -). Rotation 0: col + = lot +x, row + = lot +z.
    public sealed class LayoutItem
    {
        public char Ch;
        public int X, Z;   // game tile
        public int Dir;    // facing, auto: back to the wall (see AutoDir)
    }

    public sealed class LayoutPlan
    {
        public int W, D;
        public List<int[]> Edges = new List<int[]>();  // unit edges {x0, z0, x1, z1}, x0 <= x1, z0 <= z1
        public List<int[]> Runs = new List<int[]>();   // the same edges joined into straight drags
        public List<LayoutItem> Items = new List<LayoutItem>();
        public int SimX = -1, SimZ = -1;

        public bool HasEdge(int x0, int z0, int x1, int z1)
        {
            foreach (int[] e in Edges) if (e[0] == x0 && e[1] == z0 && e[2] == x1 && e[3] == z1) return true;
            return false;
        }

        // the wall edge on side d of tile (x, z) exists
        public bool WallOn(int x, int z, int d)
        {
            switch (d)
            {
                case 0: return HasEdge(x, z, x + 1, z);
                case 1: return HasEdge(x + 1, z, x + 1, z + 1);
                case 2: return HasEdge(x, z + 1, x + 1, z + 1);
                default: return HasEdge(x, z, x, z + 1);
            }
        }

        // Facing away from a wall so the object's back is on it: first the wall shared with X (Sim and object on opposite
        // faces of one thin wall), else the first wall side N, W, E, S; no wall: S.
        public int AutoDir(int x, int z)
        {
            for (int d = 0; d < 4; d++)
                if (SimX == x + Dx(d) && SimZ == z + Dz(d) && WallOn(x, z, d)) return (d + 2) % 4;
            foreach (int d in new int[] { 0, 3, 1, 2 })
                if (WallOn(x, z, d)) return (d + 2) % 4;
            return 2;
        }

        public static int Dx(int d) { return d == 1 ? 1 : d == 3 ? -1 : 0; }
        public static int Dz(int d) { return d == 0 ? -1 : d == 2 ? 1 : 0; }

        public string Describe()
        {
            StringBuilder s = new StringBuilder();
            s.Append("size ").Append(W).Append('x').Append(D).Append(", runs");
            foreach (int[] r in Runs) s.Append(" (").Append(r[0]).Append(',').Append(r[1]).Append(")-(").Append(r[2]).Append(',').Append(r[3]).Append(')');
            foreach (LayoutItem i in Items) s.Append(", ").Append(i.Ch).Append(" tile ").Append(i.X).Append(',').Append(i.Z).Append(" faces ").Append("NESW"[i.Dir]);
            if (SimX >= 0) s.Append(", X tile ").Append(SimX).Append(',').Append(SimZ);
            return s.ToString();
        }
    }

    public static class LayoutMap
    {
        static bool IsWall(char c) { return c == 'w' || c == 'W'; }
        static bool IsEmpty(char c) { return c == 'e' || c == 'E'; }

        static List<string> Rows(IEnumerable<string> lines)
        {
            List<string> rows = new List<string>();
            foreach (string line in lines)
            {
                string r = line.Replace(" ", "").Replace("\t", "").Replace("\r", "");
                if (r.Length > 0) rows.Add(r);
            }
            if (rows.Count == 0) throw new ArgumentException("empty map");
            foreach (string r in rows)
                if (r.Length != rows[0].Length) throw new ArgumentException("map rows differ in length: '" + rows[0] + "' and '" + r + "'");
            return rows;
        }

        // straight run of 2+ w tiles in the line, all other tiles empty; an object on such a line is an error
        static bool WallLine(string line, string what)
        {
            int run = 0, best = 0;
            foreach (char c in line) { run = IsWall(c) ? run + 1 : 0; best = Math.Max(best, run); }
            if (best < 2) return false;
            foreach (char c in line)
                if (!IsWall(c) && !IsEmpty(c)) throw new ArgumentException(what + " is a wall line and holds '" + c + "': a thin wall takes no tile, move it next to the wall");
            return true;
        }

        // items: legend chars -> facing (0..3) or -1 for auto. Any other char is an error.
        public static LayoutPlan Parse(IEnumerable<string> lines, IDictionary<char, int> items)
        {
            List<string> m = Rows(lines);
            int rows = m.Count, cols = m[0].Length;
            bool[] wallCol = new bool[cols], wallRow = new bool[rows];
            for (int r = 0; r < rows; r++) wallRow[r] = WallLine(m[r], "row " + r);
            for (int c = 0; c < cols; c++)
            {
                StringBuilder s = new StringBuilder();
                for (int r = 0; r < rows; r++) s.Append(m[r][c]);
                wallCol[c] = WallLine(s.ToString(), "column " + c);
            }
            int[] gx = new int[cols + 1], gz = new int[rows + 1];  // game index of a kept column/row, grid line of a wall one
            for (int c = 0; c < cols; c++) gx[c + 1] = gx[c] + (wallCol[c] ? 0 : 1);
            for (int r = 0; r < rows; r++) gz[r + 1] = gz[r] + (wallRow[r] ? 0 : 1);
            LayoutPlan p = new LayoutPlan();
            p.W = gx[cols];
            p.D = gz[rows];
            if (p.W < 1 || p.D < 1) throw new ArgumentException("map has no tiles left once wall lines take no tile");
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    char ch = m[r][c];
                    if (IsWall(ch))
                    {
                        if (!wallCol[c] && !wallRow[r]) throw new ArgumentException("wall tile at row " + r + " column " + c + " has no run of 2: draw at least 2 tiles or leave it out");
                        if (wallCol[c] && !wallRow[r]) AddEdge(p, gx[c], gz[r], gx[c], gz[r] + 1);
                        else if (wallRow[r] && !wallCol[c]) AddEdge(p, gx[c], gz[r], gx[c] + 1, gz[r]);
                        continue;
                    }
                    if (IsEmpty(ch)) continue;
                    if (ch == 'X')
                    {
                        if (p.SimX >= 0) throw new ArgumentException("more than one X");
                        p.SimX = gx[c];
                        p.SimZ = gz[r];
                        continue;
                    }
                    int dir;
                    if (!items.TryGetValue(ch, out dir)) throw new ArgumentException("unknown map char '" + ch + "' at row " + r + " column " + c + " (add it to legend)");
                    LayoutItem it = new LayoutItem();
                    it.Ch = ch;
                    it.X = gx[c];
                    it.Z = gz[r];
                    it.Dir = dir;
                    p.Items.Add(it);
                }
            foreach (LayoutItem i in p.Items) if (i.Dir < 0) i.Dir = p.AutoDir(i.X, i.Z);
            JoinRuns(p);
            return p;
        }

        static void AddEdge(LayoutPlan p, int x0, int z0, int x1, int z1)
        {
            if (!p.HasEdge(x0, z0, x1, z1)) p.Edges.Add(new int[] { x0, z0, x1, z1 });
        }

        static void JoinRuns(LayoutPlan p)
        {
            List<int[]> e = new List<int[]>(p.Edges);
            e.Sort(delegate(int[] a, int[] b)
            {
                int ha = a[1] == a[3] ? 1 : 0, hb = b[1] == b[3] ? 1 : 0;  // verticals first, then by line, then along it
                if (ha != hb) return ha - hb;
                int la = ha == 1 ? a[1] : a[0], lb = ha == 1 ? b[1] : b[0];
                if (la != lb) return la - lb;
                return (ha == 1 ? a[0] : a[1]) - (ha == 1 ? b[0] : b[1]);
            });
            foreach (int[] x in e)
            {
                int[] last = p.Runs.Count > 0 ? p.Runs[p.Runs.Count - 1] : null;
                bool sameDir = last != null && (last[1] == last[3]) == (x[1] == x[3]);
                if (sameDir && last[2] == x[0] && last[3] == x[1] && (x[1] == x[3] ? last[1] == x[1] : last[0] == x[0])) { last[2] = x[2]; last[3] = x[3]; }
                else p.Runs.Add((int[])x.Clone());
            }
        }

        // ---------------------------------------------------------------- map -> lot, rotation 0, 90, 180, 270

        // vertex (i, j) relative to the origin; 90 = col + runs along lot +z, row + along lot -x
        public static int[] Vertex(int i, int j, int rot)
        {
            switch (rot)
            {
                case 0: return new int[] { i, j };
                case 90: return new int[] { -j, i };
                case 180: return new int[] { -i, -j };
                case 270: return new int[] { j, -i };
                default: throw new ArgumentException("rotation must be 0, 90, 180 or 270");
            }
        }

        public static int[] Tile(int x, int z, int rot)
        {
            int[] a = Vertex(x, z, rot), b = Vertex(x + 1, z + 1, rot);
            return new int[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]) };
        }

        // {dx, dz} on the lot of map direction d
        public static int[] DirOnLot(int d, int rot) { return Vertex(LayoutPlan.Dx(d), LayoutPlan.Dz(d), rot); }

        // {minx, minz, maxx, maxz} of the footprint's vertices relative to the origin
        public static int[] Bounds(int W, int D, int rot)
        {
            int[] a = Vertex(0, 0, rot), b = Vertex(W, D, rot);
            return new int[] { Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[0], b[0]), Math.Max(a[1], b[1]) };
        }
    }
}
