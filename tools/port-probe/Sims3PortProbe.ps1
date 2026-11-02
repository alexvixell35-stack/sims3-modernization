<#
Sims 3 port probe (docs/68 §7.4, docs/PLAYERS.md "Help port to the EA app version").

Windows only, on purpose: it reads the memory of the running game, which only Windows' own process API can do.
Runs on the PowerShell that comes with Windows (5.1) or PowerShell 7; no admin, no install, no network.

What it does, read only:
  1. finds the running game (TS3.exe or TS3W.exe, any edition) you started to the main menu;
  2. waits until the game has unpacked its code in memory (the EA app exe is encrypted on disk);
  3. compares that code with hashed signatures of the Steam 1.67 exe (port-sigs.txt next to this script) and collects
     numbers about it: addresses, operand values, function sizes and call links, imported function names, string texts
     the code uses, script assembly method sizes and hashes;
  4. writes Sims3PortReport-<id>.zip to your Desktop. Send that file to Rodney (GitHub issue or Discord).
It never writes to the game, never sends anything itself, and the report holds no game code, no paths, no user or
machine name. No AI is involved anywhere; this is a plain script.

  powershell -ExecutionPolicy Bypass -File Sims3PortProbe.ps1              normal use (or double-click Run-PortProbe.cmd)
  ... -Questions questions.json        answer a follow-up file from Rodney (numbers only), writes Sims3PortAnswers-<id>.zip
  ... -SelfCheck                       offline check of the matcher on a synthetic buffer
Developer options (never for sharing):
  ... -ImageFile <exe> [-AssemblyDir <dir>] [-ReportOut <file>]   run on an exe file (the Steam exe: identity check)
  ... -DumpOut <file>                  also write the unpacked exe image (contains game code: keep it on your own PC)
#>
param(
    [switch]$SelfCheck,
    [string]$ImageFile,
    [string]$AssemblyDir,
    [string]$ReportOut,
    [string]$DumpOut,
    [string]$SigFile,
    [string]$Questions,
    [int]$NoProgressSec = 30
)
$ErrorActionPreference = 'Stop'

$cs = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

public class S3Image { public byte[] Img; public uint PrefBase, RunBase; public List<int[]> Exec = new List<int[]>(); public int Unreadable; }

public static class S3Port {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(IntPtr h, int f, StringBuilder s, ref int n);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int c, ref IntPtr info, int len, IntPtr ret);

    const int CAP = 256, K = 16, CORE = 32, MAXHIT = 64, MAXCAND = 16;
    const uint S1 = 0x53334d31, S2 = 0x706f7274;

    public static uint U32(byte[] b, long p) { return (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)); }
    public static int I32(byte[] b, long p) { return b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24); }
    public static int U16(byte[] b, long p) { return b[p] | (b[p + 1] << 8); }
    static string H(long v) { return v.ToString("X"); }

    // ---- process ----
    public static IntPtr Open(int pid) {
        IntPtr h = OpenProcess(0x10 | 0x1000, false, pid);
        if (h == IntPtr.Zero) throw new Exception("cannot open the game for reading (Windows error " + Marshal.GetLastWin32Error() + "). If the game runs as administrator, run this script as administrator too.");
        return h;
    }
    public static string ExePath(IntPtr h) { StringBuilder sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : null; }
    public static byte[] Read(IntPtr h, long addr, int n) { byte[] b = new byte[n]; IntPtr r; return ReadProcessMemory(h, new IntPtr(addr), b, new IntPtr(n), out r) && r.ToInt64() == n ? b : null; }
    // image base of a 32-bit game seen from 64-bit PowerShell: WOW64 PEB +8; 0 = unknown
    public static long ImageBase(IntPtr h) {
        if (IntPtr.Size != 8) return 0;
        IntPtr peb = IntPtr.Zero;
        if (NtQueryInformationProcess(h, 26, ref peb, IntPtr.Size, IntPtr.Zero) != 0 || peb == IntPtr.Zero) return 0;
        byte[] b = Read(h, peb.ToInt64() + 8, 4);
        return b == null ? 0 : U32(b, 0);
    }
    public static double Entropy(byte[] b, int off, int n) {
        long[] c = new long[256]; for (int i = 0; i < n; i++) c[b[off + i]]++;
        double e = 0; for (int i = 0; i < 256; i++) if (c[i] > 0) { double p = (double)c[i] / n; e -= p * Math.Log(p, 2); }
        return e;
    }
    // entropy of the first code section in memory, -1 if unreadable
    public static double CodeEntropy(IntPtr h, long bas) {
        byte[] hd = Read(h, bas, 0x1000); if (hd == null) return -1;
        int pe = (int)U32(hd, 0x3c), n = U16(hd, pe + 6), first = pe + 24 + U16(hd, pe + 20);
        for (int i = 0; i < n; i++) {
            int s = first + 40 * i; uint ch = U32(hd, s + 36);
            if ((ch & 0x20000000) == 0) continue;
            uint rva = U32(hd, s + 12), sz = Math.Min(U32(hd, s + 8), 0x400000u);
            byte[] t = ReadSome(h, bas + rva, (int)sz); return t == null ? -1 : Entropy(t, 0, t.Length);
        }
        return -1;
    }
    static byte[] ReadSome(IntPtr h, long a, int n) { int got = 0; byte[] o = new byte[n]; for (int p = 0; p < n; p += 0x1000) { int m = Math.Min(0x1000, n - p); byte[] b = Read(h, a + p, m); if (b != null) { Array.Copy(b, 0, o, p, m); got++; } } return got == 0 ? null : o; }

    static void SetExec(S3Image im) {
        byte[] b = im.Img; int pe = (int)U32(b, 0x3c), n = U16(b, pe + 6), first = pe + 24 + U16(b, pe + 20);
        for (int i = 0; i < n; i++) { int s = first + 40 * i; if ((U32(b, s + 36) & 0x20000000) != 0) { uint rva = U32(b, s + 12); im.Exec.Add(new int[] { (int)rva, (int)(rva + Math.Max(U32(b, s + 8), U32(b, s + 16))) }); } }
    }
    public static S3Image FromFile(string path) {
        byte[] buf = File.ReadAllBytes(path);
        int pe = (int)U32(buf, 0x3c), nsec = U16(buf, pe + 6), opt = pe + 24, optSize = U16(buf, pe + 20);
        uint size = U32(buf, opt + 56), hdr = U32(buf, opt + 60);
        S3Image im = new S3Image(); im.Img = new byte[size];
        Array.Copy(buf, 0, im.Img, 0, (int)Math.Min(hdr, (uint)buf.Length));
        for (int i = 0, s = opt + optSize; i < nsec; i++, s += 40) {
            uint vsz = U32(buf, s + 8), rva = U32(buf, s + 12), raw = U32(buf, s + 16), ptr = U32(buf, s + 20);
            long n = Math.Min(raw, vsz != 0 ? vsz : raw); n = Math.Min(n, (long)buf.Length - ptr); n = Math.Min(n, (long)size - rva);
            if (n > 0) Array.Copy(buf, ptr, im.Img, rva, n);
        }
        im.PrefBase = im.RunBase = U32(buf, opt + 28); SetExec(im); return im;
    }
    public static S3Image FromProcess(IntPtr h, long bas, uint prefBase) {
        byte[] hd = Read(h, bas, 0x1000); if (hd == null) throw new Exception("cannot read the game's exe header");
        int pe = (int)U32(hd, 0x3c); uint size = U32(hd, pe + 24 + 56);
        S3Image im = new S3Image(); im.Img = new byte[size];
        for (long p = 0; p < size; p += 0x10000) {
            int m = (int)Math.Min(0x10000, size - p); byte[] b = Read(h, bas + p, m);
            if (b != null) { Array.Copy(b, 0, im.Img, p, m); continue; }
            for (long q = p; q < p + m; q += 0x1000) { int k = (int)Math.Min(0x1000, p + m - q); byte[] c = Read(h, bas + q, k); if (c != null) Array.Copy(c, 0, im.Img, q, k); else im.Unreadable++; }
        }
        im.RunBase = (uint)bas; im.PrefBase = prefBase != 0 ? prefBase : (uint)bas; SetExec(im); return im;
    }
    // dev only: the memory image as a PE file (raw offsets = RVAs) for tools/exe-addr-map.ts --other
    public static void WritePe(S3Image im, string path) {
        byte[] b = (byte[])im.Img.Clone(); int pe = (int)U32(b, 0x3c), n = U16(b, pe + 6), first = pe + 24 + U16(b, pe + 20);
        for (int i = 0; i < n; i++) { int s = first + 40 * i; uint v = U32(b, s + 8), rva = U32(b, s + 12); uint sz = v != 0 ? v : U32(b, s + 16); Put(b, s + 16, sz); Put(b, s + 20, rva); }
        Put(b, pe + 24 + 28, im.PrefBase); File.WriteAllBytes(path, b);
    }
    static void Put(byte[] b, int p, uint v) { b[p] = (byte)v; b[p + 1] = (byte)(v >> 8); b[p + 2] = (byte)(v >> 16); b[p + 3] = (byte)(v >> 24); }
    public static string CodeSha(S3Image im) {
        using (SHA256 sh = SHA256.Create()) { foreach (int[] e in im.Exec) { int a = e[0], n = Math.Min(e[1], im.Img.Length) - a; if (n > 0) sh.TransformBlock(im.Img, a, n, null, 0); } sh.TransformFinalBlock(new byte[0], 0, 0); return BitConverter.ToString(sh.Hash).Replace("-", "").ToLowerInvariant(); }
    }

    // ---- hashes (tools/port-sigs.ts mm3 / key48) ----
    static uint Rotl(uint x, int r) { return (x << r) | (x >> (32 - r)); }
    public static uint MM3(byte[] b, int at, uint seed) {
        uint h = seed;
        for (int i = 0; i < 16; i += 4) { uint k = U32(b, at + i); k *= 0xcc9e2d51; k = Rotl(k, 15); k *= 0x1b873593; h ^= k; h = Rotl(h, 13); h = h * 5 + 0xe6546b64; }
        h ^= 16; h ^= h >> 16; h *= 0x85ebca6b; h ^= h >> 13; h *= 0xc2b2ae35; h ^= h >> 16; return h;
    }
    public static long Key48(byte[] b, int at) { return (long)MM3(b, at, S1) * 65536 + (MM3(b, at, S2) & 0xffff); }
    public static string SigId(string text) { using (SHA256 sh = SHA256.Create()) return BitConverter.ToString(sh.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r", "")))).Replace("-", "").Substring(0, 8).ToLowerInvariant(); }

    // ---- signature file ----
    public class Sig { public uint Base; public long[] TR, TK; public int[] FR, FS, FI; public char[] FK; public long[] SV; public int[] Tlo, Thi, Flo, Fhi; }
    public static Sig ParseSig(string text) {
        List<string> L = new List<string>();
        foreach (string l in text.Replace("\r", "").Split('\n')) if (l.Length > 0 && l[0] != '#') L.Add(l);
        string[] head = L[0].Split(' ');
        if (head[0] != "steam" || head[5] != "256" || head[7] != "16" || head[9] != "32") throw new Exception("port-sigs.txt is not a v1 signature file");
        Sig s = new Sig(); s.Base = Convert.ToUInt32(head[3], 16); int i = 1;
        int n = int.Parse(L[i++].Substring(2)); s.TR = new long[n]; s.TK = new long[n];
        for (int j = 0; j < n; j++) { string[] w = L[i++].Split(' '); s.TR[j] = Convert.ToInt64(w[0], 16); s.TK[j] = Convert.ToInt64(w[1], 16); }
        n = int.Parse(L[i++].Substring(2)); s.FR = new int[n]; s.FS = new int[n]; s.FK = new char[n]; s.FI = new int[n];
        for (int j = 0; j < n; j++) { string[] w = L[i++].Split(' '); s.FR[j] = Convert.ToInt32(w[0], 16); s.FS[j] = w[1][0] - '0'; s.FK[j] = w[1][1]; s.FI[j] = Convert.ToInt32(w[1].Substring(2), 16); }
        n = int.Parse(L[i++].Substring(2)); s.SV = new long[n]; s.Tlo = new int[n]; s.Thi = new int[n]; s.Flo = new int[n]; s.Fhi = new int[n];
        for (int j = 0; j < n; j++) { string[] w = L[i++].Split(' '); s.SV[j] = Convert.ToInt64(w[0], 16); s.Tlo[j] = int.Parse(w[1]); s.Thi[j] = int.Parse(w[2]); s.Flo[j] = int.Parse(w[3]); s.Fhi[j] = int.Parse(w[4]); }
        return s;
    }

    // ---- matcher (same lines as tools/port-sigs.ts matchImage) ----
    static long UN(byte[] b, int p, int n) { long v = 0; for (int i = n - 1; i >= 0; i--) v = v * 256 + b[p + i]; return v; }
    public static List<string> Match(S3Image im, Sig sig) {
        byte[] b = im.Img; int len = b.Length;
        HashSet<long> want = new HashSet<long>(); HashSet<uint> h1s = new HashSet<uint>();
        foreach (long k in sig.TK) { want.Add(k); h1s.Add((uint)(k / 65536)); }
        Dictionary<long, List<int>> hits = new Dictionary<long, List<int>>();
        foreach (int[] e in im.Exec) {
            int end = Math.Min(e[1], len);
            for (int p = e[0]; p + K <= end; p++) {
                uint h1 = MM3(b, p, S1); if (!h1s.Contains(h1)) continue;
                long k = (long)h1 * 65536 + (MM3(b, p, S2) & 0xffff); if (!want.Contains(k)) continue;
                List<int> l; if (!hits.TryGetValue(k, out l)) { l = new List<int>(); hits[k] = l; }
                if (l.Count <= MAXHIT) l.Add(p);
            }
        }
        List<string> o = new List<string>(); int nu = 0, na = 0, nm = 0;
        for (int si = 0; si < sig.SV.Length; si++) {
            long rva = sig.SV[si] - sig.Base;
            List<int> ts = new List<int>(); for (int t = sig.Tlo[si]; t < sig.Thi[si]; t++) ts.Add(t);
            Func<int, int> off = t => (int)(sig.TR[t] - rva);
            ts.Sort((x, y) => { int c = Math.Abs(2 * off(x) + K).CompareTo(Math.Abs(2 * off(y) + K)); return c != 0 ? c : off(x).CompareTo(off(y)); });
            HashSet<int> cand = new HashSet<int>();
            foreach (int t in ts) { List<int> l; if (hits.TryGetValue(sig.TK[t], out l) && l.Count <= MAXHIT) foreach (int p in l) { int c = p - off(t); if (c >= 0 && c < len) cand.Add(c); } }
            if (cand.Count == 0) { o.Add("S " + si + " -"); nm++; continue; }
            List<int> cs = new List<int>(cand); cs.Sort();
            List<int> mis = new List<int>(); int lo = 0, hi = 0;
            for (int i = 0; i < ts.Count; i++) {
                int t = ts[i], ot = off(t); List<int> f = new List<int>();
                foreach (int x in cs) { int q = x + ot; if (q >= 0 && q + K <= len && Key48(b, q) == sig.TK[t]) f.Add(x); }
                if (f.Count > 0) cs = f; else mis.Add(t);
                if (i == 0) { lo = ot; hi = ot + K; } else { lo = Math.Min(lo, ot); hi = Math.Max(hi, ot + K); }
                if (cs.Count == 1 && (i + 1 >= ts.Count || !(off(ts[i + 1]) < CORE && off(ts[i + 1]) + K > -CORE))) break;
            }
            if (cs.Count > 1) {
                StringBuilder sb = new StringBuilder("S " + si + " a " + cs.Count);
                for (int j = 0; j < cs.Count && j < MAXCAND; j++) sb.Append(" " + H(im.PrefBase + (long)cs[j]));
                o.Add(sb.ToString()); na++; continue;
            }
            nu++; int nn = cs[0]; mis.Sort();
            o.Add("S " + si + " u " + H(im.PrefBase + (long)nn) + " " + lo + " " + hi + " " + (mis.Count > 0 ? string.Join(",", mis.ConvertAll(x => x.ToString()).ToArray()) : "-"));
            for (int fi = sig.Flo[si]; fi < sig.Fhi[si]; fi++) {
                int ofs = (int)(sig.FR[fi] - rva), p = nn + ofs, sz = sig.FS[fi]; char kd = sig.FK[fi];
                if (p < 0 || p + sz > len) continue;
                long v;
                if (kd == 'r' || kd == 'a') {
                    if (!((ofs >= lo && ofs + sz <= hi) || (ofs >= 0 && ofs < 16))) continue;
                    v = kd == 'r' ? (uint)(im.PrefBase + p + 4 + I32(b, p)) : Norm(im, U32(b, p));
                } else {
                    bool any = false; foreach (int t in mis) if (ofs < off(t) + K && ofs + sz > off(t)) { any = true; break; }
                    if (!any) continue;
                    v = UN(b, p, sz);
                }
                o.Add("F " + si + " " + fi + " " + H(v));
            }
        }
        o.Add("summary: sites " + sig.SV.Length + " unique " + nu + " ambiguous " + na + " missing " + nm);
        return o;
    }
    static uint Norm(S3Image im, uint v) { return im.RunBase != im.PrefBase && v >= im.RunBase && v < im.RunBase + (uint)im.Img.Length ? v - im.RunBase + im.PrefBase : v; }

    // ---- x86-32 length decoder (tools/x86-len.ts) ----
    const string ONE = "mmmmbz..mmmmbz.2mmmmbz..mmmmbz..mmmmbzp.mmmmbzp.mmmmbzp.mmmmbzp...................................mmppppzZbM....rrrrrrrrrrrrrrrrMZMMmmmmmmmmmmmm..........f.....oooo....bz......bbbbbbbbzzzzzzzzMMw.mmMZe.w..b..mmmmbb..mmmmmmmmrrrrbbbbRRfr....p.pp..gG......mm";
    const string TWO = "mmmmx.....x.xm.Mmmmmmmmmmmmmmmmmmmmmxxxxmmmmmmmm......x.3xAxxxxxmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmMMMMmmm.mmxxmmmmRRRRRRRRRRRRRRRRmmmmmmmmmmmmmmmm...mMmxx...mMmmmmmmmmmmmmmMmmmmmmmMmMMMm........mmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmmm";
    static bool Pre(byte x) { return x == 0x66 || x == 0x67 || x == 0xf0 || x == 0xf2 || x == 0xf3 || x == 0x26 || x == 0x2e || x == 0x36 || x == 0x3e || x == 0x64 || x == 0x65; }
    public struct Ins { public int Len, Op, DispOff, DispSize, ImmOff, ImmSize, RelOff, RelSize; public bool Abs; }
    public static bool Decode(byte[] b, int at, int end, out Ins ins) {
        ins = new Ins(); int p = at; bool opsz = false, adsz = false;
        while (p < end && p - at < 14 && Pre(b[p])) { if (b[p] == 0x66) opsz = true; if (b[p] == 0x67) adsz = true; p++; }
        if (p >= end) return false;
        int op = b[p++]; char t = ONE[op];
        if (t == '2') {
            if (p >= end) return false;
            int c = b[p++]; t = TWO[c]; op = 0x0f00 | c;
            if (t == '3' || t == 'A') { if (p >= end) return false; op = (t == '3' ? 0x0f3800 : 0x0f3a00) | b[p++]; t = t == '3' ? 'm' : 'M'; }
        }
        if (t == 'x' || t == 'p') return false;
        int z = opsz ? 2 : 4; ins.Op = op; int reg = 0;
        if ("mMZgG".IndexOf(t) >= 0) {
            if (p >= end) return false;
            int modrm = b[p++], mod = modrm >> 6, rm = modrm & 7; reg = (modrm >> 3) & 7;
            if (mod != 3) {
                if (adsz) { if (mod == 0 && rm == 6) { ins.Abs = true; ins.DispSize = 2; } else ins.DispSize = mod == 1 ? 1 : mod == 2 ? 2 : 0; }
                else {
                    int bs = rm;
                    if (rm == 4) { if (p >= end) return false; bs = b[p++] & 7; }
                    if (mod == 0 && bs == 5) { ins.Abs = true; ins.DispSize = 4; } else ins.DispSize = mod == 1 ? 1 : mod == 2 ? 4 : 0;
                }
                if (ins.DispSize > 0) { ins.DispOff = p - at; p += ins.DispSize; }
            }
        }
        int imm = 0;
        switch (t) { case 'b': case 'M': imm = 1; break; case 'z': case 'Z': imm = z; break; case 'w': imm = 2; break; case 'e': imm = 3; break; case 'f': imm = z + 2; break; case 'g': imm = reg < 2 ? 1 : 0; break; case 'G': imm = reg < 2 ? z : 0; break; }
        if (t == 'o') { ins.DispOff = p - at; ins.DispSize = adsz ? 2 : 4; ins.Abs = true; p += ins.DispSize; }
        if (imm > 0) { ins.ImmOff = p - at; ins.ImmSize = imm; p += imm; }
        if (t == 'r' || t == 'R') { ins.RelOff = p - at; ins.RelSize = t == 'r' ? 1 : z; p += ins.RelSize; }
        if (p > end) return false;
        ins.Len = p - at; return true;
    }

    // ---- fingerprints (tools/port-fingerprints.ts) ----
    public static int InsClass(byte[] b, int at, int op) {
        int q = at; while (q - at < 14 && Pre(b[q])) q++;
        int reg = q + 1 < b.Length ? (b[q + 1] >> 3) & 7 : 0;
        if (op < 0x100) {
            if ((op >= 0x88 && op <= 0x8c) || op == 0x8e || op == 0xc6 || op == 0xc7 || (op >= 0xa0 && op <= 0xa3) || (op >= 0xb0 && op <= 0xbf)) return 0;
            if (op == 0x8d) return 1;
            if ((op >= 0x50 && op <= 0x61) || op == 0x68 || op == 0x6a || op == 0x8f || (op == 0xff && reg == 6)) return 2;
            if (op == 0xe8 || (op == 0xff && (reg == 2 || reg == 3))) return 3;
            if (op == 0xe9 || op == 0xeb || (op == 0xff && (reg == 4 || reg == 5))) return 4;
            if ((op >= 0x70 && op <= 0x7f) || (op >= 0xe0 && op <= 0xe3)) return 5;
            if (op == 0xc2 || op == 0xc3) return 6;
            if ((op >= 0x38 && op <= 0x3d) || op == 0x84 || op == 0x85 || op == 0xa8 || op == 0xa9 || ((op == 0xf6 || op == 0xf7) && reg == 0) || (op >= 0x80 && op <= 0x83 && reg == 7)) return 7;
            if ((op < 0x40 && (op & 7) < 6) || (op >= 0x40 && op <= 0x4f) || (op >= 0x80 && op <= 0x83) || op == 0xc0 || op == 0xc1 || (op >= 0xd0 && op <= 0xd3) || op == 0xf6 || op == 0xf7 || op == 0x69 || op == 0x6b) return 8;
            if (op >= 0xd8 && op <= 0xdf) return 9;
            return 11;
        }
        if (op >= 0x0f80 && op <= 0x0f8f) return 5;
        if (op == 0x0fb6 || op == 0x0fb7 || op == 0x0fbe || op == 0x0fbf) return 0;
        if (op == 0x0faf) return 8;
        if (op > 0xffff || (op >= 0x0f10 && op <= 0x0f17) || (op >= 0x0f28 && op <= 0x0f2f) || (op >= 0x0f50 && op <= 0x0f7f) || op == 0x0fc2 || op == 0x0fc6 || op >= 0x0fd0) return 10;
        return 11;
    }
    public static string Cstr(byte[] b, long p, int max) {
        StringBuilder s = new StringBuilder();
        for (int i = 0; i <= max; i++) { if (p + i < 0 || p + i >= b.Length) return null; int c = b[p + i]; if (c == 0) return s.ToString(); if (c < 0x20 || c > 0x7e) return null; s.Append((char)c); }
        return null;
    }
    class Sec { public string Name; public int Rva, Size; public uint Ch; }
    static List<Sec> Sections(byte[] b) {
        int pe = (int)U32(b, 0x3c), n = U16(b, pe + 6), first = pe + 24 + U16(b, pe + 20); List<Sec> o = new List<Sec>();
        for (int i = 0, s = first; i < n; i++, s += 40) {
            StringBuilder nm = new StringBuilder(); for (int j = 0; j < 8 && b[s + j] != 0; j++) nm.Append((char)b[s + j]);
            uint vs = U32(b, s + 8), raw = U32(b, s + 16);
            Sec x = new Sec(); x.Name = nm.ToString(); x.Rva = (int)U32(b, s + 12); x.Size = (int)(vs != 0 ? vs : raw); x.Ch = U32(b, s + 36); o.Add(x);
        }
        return o;
    }
    public static List<string> SectionLines(S3Image im) {
        List<string> o = new List<string>();
        foreach (Sec s in Sections(im.Img)) { int n = Math.Max(0, Math.Min(s.Size, im.Img.Length - s.Rva)); o.Add("section: name=" + (s.Name.Length > 0 ? s.Name : "-") + " rva=0x" + H(s.Rva) + " size=0x" + H(s.Size) + " ch=0x" + H(s.Ch) + " entropy=" + (n > 0 ? Entropy(im.Img, s.Rva, n).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-")); }
        return o;
    }

    public static List<string> Fingerprints(S3Image im) {
        byte[] b = im.Img; int len = b.Length; long pb = im.PrefBase; List<Sec> secs = Sections(b);
        Func<long, Sec> secOf = r => { foreach (Sec s in secs) if (r >= s.Rva && r < (long)s.Rva + s.Size) return s; return null; };
        Func<long, bool> isExec = r => { if (r < 0 || r >= len) return false; Sec s = secOf(r); return s != null && (s.Ch & 0x20000000) != 0; };
        Func<long, string> strAt = va => { long r = va - pb; if (r < 0 || r >= len || isExec(r) || secOf(r) == null) return null; string t = Cstr(b, r, 120); return t != null && t.Length >= 4 ? t : null; };
        List<Sec> dataSecs = secs.FindAll(s => (s.Ch & 0x20000000) == 0);
        int pe = (int)U32(b, 0x3c), opt = pe + 24;
        Dictionary<long, int> imp = new Dictionary<long, int>(); List<string> impNames = new List<string>();
        uint idir = U32(b, opt + 96 + 8);
        if (idir != 0) for (long d = idir; d + 20 <= len; d += 20) {
            uint oft = U32(b, d), nm = U32(b, d + 12), ft = U32(b, d + 16);
            if (nm == 0 && ft == 0) break;
            string dll = (Cstr(b, nm, 64) ?? "?").ToLowerInvariant(); uint th = oft != 0 ? oft : ft;
            for (long i = 0; th + 4 * i + 4 <= len && i < 4096; i++) {
                uint e = U32(b, th + 4 * i); if (e == 0) break;
                string name = (e & 0x80000000) != 0 ? "#" + (e & 0xffff) : e + 2 < len ? Cstr(b, e + 2, 120) ?? "?" : "?";
                imp[pb + ft + 4 * i] = impNames.Count; impNames.Add(dll + "!" + name);
            }
        }
        Func<long, bool> fnStart = r => isExec(r) && r >= 3 && (b[r - 1] == 0xcc || b[r - 1] == 0xc3 || b[r - 1] == 0x90 || b[r - 3] == 0xc2);
        Dictionary<long, string> tds = new Dictionary<long, string>();
        foreach (Sec s in dataSecs) { long e = Math.Min(len, (long)s.Rva + s.Size) - 4; for (long p = s.Rva; p < e; p++) if (b[p] == 0x2e && b[p + 1] == 0x3f && b[p + 2] == 0x41 && (b[p + 3] == 0x56 || b[p + 3] == 0x55) && p - 8 >= s.Rva) { string n = Cstr(b, p, 200); if (n != null) tds[pb + p - 8] = n; } }
        Dictionary<long, string> cols = new Dictionary<long, string>(); List<string> rtti = new List<string>(); List<long[]> rslots = new List<long[]>();
        foreach (Sec s in dataSecs) { long e = Math.Min(len, (long)s.Rva + s.Size) - 4; for (long q = s.Rva + 12; q <= e; q += 4) { string n; if (tds.TryGetValue(Norm(im, U32(b, q)), out n) && U32(b, q - 12) == 0) cols[pb + q - 12] = n; } }
        foreach (Sec s in dataSecs) {
            long lim = Math.Min(len, (long)s.Rva + s.Size), e = lim - 4;
            for (long q = s.Rva; q < e; q += 4) {
                string n; if (!cols.TryGetValue(Norm(im, U32(b, q)), out n)) continue;
                List<long> sl = new List<long>();
                for (long k = q + 4; k + 4 <= lim && sl.Count < 1024; k += 4) { long v = Norm(im, U32(b, k)); if (!isExec(v - pb)) break; sl.Add(v); }
                rtti.Add("R " + H(pb + q + 4) + " " + sl.Count + " " + n); rslots.Add(sl.ToArray());
            }
        }
        List<string> ics = new List<string>(); List<long> icf = new List<long>();
        foreach (Sec s in dataSecs) { long e = Math.Min(len, (long)s.Rva + s.Size) - 8; for (long q = s.Rva; q <= e; q += 4) { long f = Norm(im, U32(b, q)); if (!isExec(f - pb)) continue; string t = strAt(Norm(im, U32(b, q + 4))); if (t != null && t.Contains("::")) { ics.Add("I " + H(f) + " " + H(pb + q) + " " + t); icf.Add(f); } } }
        HashSet<long> starts = new HashSet<long>();
        foreach (Sec s in secs) { if ((s.Ch & 0x20000000) == 0) continue; long e = Math.Min(len, (long)s.Rva + s.Size); for (long p = s.Rva; p + 5 <= e; p++) if (b[p] == 0xe8) { long t = p + 5 + I32(b, p + 1); if (fnStart(t)) starts.Add(t); } }
        foreach (long[] sl in rslots) foreach (long f in sl) if (fnStart(f - pb)) starts.Add(f - pb);
        foreach (long f in icf) if (isExec(f - pb)) starts.Add(f - pb);
        uint ep = U32(b, opt + 16); if (ep != 0 && isExec(ep)) starts.Add(ep);
        List<string> vts = new List<string>();
        foreach (Sec s in dataSecs) {
            long lim = Math.Min(len, (long)s.Rva + s.Size); List<long> cur = new List<long>(); long at = 0;
            for (long q = s.Rva; q + 4 <= lim; q += 4) {
                long v = Norm(im, U32(b, q));
                if (fnStart(v - pb)) { if (cur.Count == 0) at = q; cur.Add(v); starts.Add(v - pb); }
                else { if (cur.Count >= 2) vts.Add("W " + H(pb + at) + " " + string.Join(",", cur.ConvertAll(x => H(x)).ToArray())); cur.Clear(); }
            }
            if (cur.Count >= 2) vts.Add("W " + H(pb + at) + " " + string.Join(",", cur.ConvertAll(x => H(x)).ToArray()));
        }
        List<long> st = new List<long>(starts); st.Sort();
        Dictionary<long, int> strIdx = new Dictionary<long, int>(); List<string> strs = new List<string>(), outp = new List<string>();
        for (int i = 0; i < st.Count; i++) {
            long s = st[i]; Sec sec = secOf(s);
            long end = Math.Min(Math.Min(i + 1 < st.Count ? st[i + 1] : len, s + 0x8000), Math.Min((long)sec.Rva + sec.Size, len));
            int[] hist = new int[12]; SortedSet<long> callees = new SortedSet<long>(), imps = new SortedSet<long>(), ss = new SortedSet<long>(), gl = new SortedSet<long>();
            long p = s; int n = 0; Ins d;
            while (p < end) {
                if (!Decode(b, (int)p, (int)end, out d)) break;
                n++; hist[InsClass(b, (int)p, d.Op)]++;
                if (d.Op == 0xe8 && d.RelSize == 4) { long t = p + d.Len + I32(b, p + d.RelOff); if (isExec(t)) callees.Add(pb + t); }
                List<long> vals = new List<long>();
                if (d.Abs && d.DispSize == 4) vals.Add(Norm(im, U32(b, p + d.DispOff)));
                if (d.ImmSize == 4) vals.Add(Norm(im, U32(b, p + d.ImmOff)));
                foreach (long v in vals) {
                    int ii; if (imp.TryGetValue(v, out ii)) { imps.Add(ii); continue; }
                    string t = strAt(v);
                    if (t != null) { int k; if (!strIdx.TryGetValue(v, out k)) { k = strs.Count; strIdx[v] = k; strs.Add("Z " + k + " " + H(v) + " " + t); } ss.Add(k); continue; }
                    long r = v - pb; Sec sc = r >= 0 && r < len ? secOf(r) : null;
                    if (sc != null && (sc.Ch & 0x80000000) != 0 && (sc.Ch & 0x20000000) == 0) gl.Add(v);
                }
                p += d.Len;
            }
            outp.Add("P " + H(pb + s) + " " + (end - s) + " " + n + " " + string.Join(",", Array.ConvertAll(hist, x => x.ToString())) + " " + L(callees, true) + " " + L(imps, false) + " " + L(ss, false) + " " + L(gl, true));
        }
        List<string> o = new List<string>();
        for (int i = 0; i < impNames.Count; i++) o.Add("M " + i + " " + impNames[i]);
        o.AddRange(strs); o.AddRange(outp); o.AddRange(rtti); o.AddRange(vts); o.AddRange(ics);
        return o;
    }
    static string L(SortedSet<long> x, bool hx) { if (x.Count == 0) return "-"; List<string> o = new List<string>(); foreach (long v in x) o.Add(hx ? H(v) : v.ToString()); return string.Join(",", o.ToArray()); }

    // ---- script assemblies (tools/port-fingerprints.ts methodTable, tools/extract-assemblies.ts) ----
    static readonly Dictionary<string, int[]> CODED = new Dictionary<string, int[]> {
        { "TypeDefOrRef", new[] { 2, 1, 0x1b } }, { "HasConstant", new[] { 4, 8, 0x17 } },
        { "HasCustomAttribute", new[] { 6, 4, 1, 2, 8, 9, 10, 0, 0x0e, 0x17, 0x14, 0x11, 0x1a, 0x1b, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2a, 0x2c, 0x2b } },
        { "HasFieldMarshal", new[] { 4, 8 } }, { "HasDeclSecurity", new[] { 2, 6, 0x20 } }, { "MemberRefParent", new[] { 2, 1, 0x1a, 6, 0x1b } }, { "HasSemantics", new[] { 0x14, 0x17 } },
        { "MethodDefOrRef", new[] { 6, 0x0a } }, { "MemberForwarded", new[] { 4, 6 } }, { "Implementation", new[] { 0x26, 0x23, 0x27 } }, { "CustomAttributeType", new[] { -1, -1, 6, 0x0a, -1 } },
        { "ResolutionScope", new[] { 0, 0x1a, 0x23, 1 } }, { "TypeOrMethodDef", new[] { 2, 6 } } };
    const string SCHEMA = "0:2 s g g g|1:cResolutionScope s s|2:4 s s cTypeDefOrRef t4 t6|3:t4|4:2 s b|5:t6|6:4 2 2 s b t8|7:t8|8:2 2 s|9:t2 cTypeDefOrRef|10:cMemberRefParent s b|11:2 cHasConstant b|12:cHasCustomAttribute cCustomAttributeType b|13:cHasFieldMarshal b|14:2 cHasDeclSecurity b|15:2 4 t2|16:4 t4|17:b|18:t2 t20|19:t20|20:2 s cTypeDefOrRef|21:t2 t23|22:t23|23:2 s b|24:2 t6 cHasSemantics|25:t2 cMethodDefOrRef cMethodDefOrRef|26:s|27:b|28:2 cMemberForwarded s t26|29:4 t4|30:4 4|31:4|32:4 2 2 2 2 4 b s s|33:4|34:4 4 4|35:2 2 2 2 4 b s s b|36:4 t35|37:4 4 4 t35|38:4 s b|39:4 4 s s cImplementation|40:4 4 s cImplementation|41:t2 t2|42:2 2 cTypeOrMethodDef s|43:cMethodDefOrRef b|44:t42 cTypeDefOrRef";
    static uint Fnv32(byte[] b, int p, int n) { uint x = 0x811c9dc5; for (int i = 0; i < n; i++) x = (x ^ b[p + i]) * 0x01000193; return x; }
    public static List<string> MethodTable(byte[] buf) {
        int pe = (int)U32(buf, 0x3c), nsec = U16(buf, pe + 6), opt = pe + 24, first = opt + U16(buf, pe + 20);
        Func<long, long> off = r => { for (int i = 0; i < nsec; i++) { int s = first + 40 * i; long va = U32(buf, s + 12), sz = Math.Max(U32(buf, s + 8), U32(buf, s + 16)); if (r >= va && r < va + sz) return r - va + U32(buf, s + 20); } return -1; };
        long cli = off(U32(buf, opt + 96 + 14 * 8)), md = off(U32(buf, cli + 8));
        long p = md + 16 + U32(buf, md + 12) + 2; int ns = U16(buf, p); p += 2;
        Dictionary<string, long> st = new Dictionary<string, long>();
        for (int i = 0; i < ns; i++) { long so = U32(buf, p); p += 8; long e = p; while (buf[e] != 0) e++; st[Encoding.ASCII.GetString(buf, (int)p, (int)(e - p))] = md + so; p = (e + 4) & ~3L; }
        long t = st["#~"]; int heap = buf[t + 6]; long[] rows = new long[64]; p = t + 24;
        for (int i = 0; i < 64; i++) if (((i < 32 ? U32(buf, t + 8) >> i : U32(buf, t + 12) >> (i - 32)) & 1) != 0) { rows[i] = U32(buf, p); p += 4; }
        string[][] schema = new string[64][];
        foreach (string x in SCHEMA.Split('|')) { string[] w = x.Split(':'); schema[int.Parse(w[0])] = w[1].Split(' '); }
        Func<string, int> size = c => {
            int v; if (int.TryParse(c, out v)) return v;
            if (c == "s") return (heap & 1) != 0 ? 4 : 2; if (c == "g") return (heap & 2) != 0 ? 4 : 2; if (c == "b") return (heap & 4) != 0 ? 4 : 2;
            if (c[0] == 't') return rows[int.Parse(c.Substring(1))] < 0x10000 ? 2 : 4;
            int[] tabs = CODED[c.Substring(1)]; long mx = 0; foreach (int x in tabs) mx = Math.Max(mx, x < 0 ? 0 : rows[x]);
            int bits = (int)Math.Ceiling(Math.Log(tabs.Length, 2)); return mx < (1L << (16 - bits)) ? 2 : 4;
        };
        long[][][] tab = new long[64][][];
        for (int ti = 0; ti < 64; ti++) {
            if (rows[ti] == 0) continue;
            if (schema[ti] == null) throw new Exception("unknown metadata table " + ti);
            int[] sz = Array.ConvertAll(schema[ti], x => size(x)); tab[ti] = new long[rows[ti]][];
            for (long r = 0; r < rows[ti]; r++) { long[] row = new long[sz.Length]; for (int k = 0; k < sz.Length; k++) { row[k] = sz[k] == 1 ? (long)buf[p] : sz[k] == 2 ? (long)U16(buf, p) : (long)U32(buf, p); p += sz[k]; } tab[ti][r] = row; }
        }
        Func<long, string> str = i => { long s0 = st["#Strings"] + i, e = s0; while (buf[e] != 0) e++; return Encoding.UTF8.GetString(buf, (int)s0, (int)(e - s0)); };
        long[] mod = tab[0][0]; long g = st["#GUID"] + (mod[2] - 1) * 16;
        string mvid = BitConverter.ToString(buf, (int)g, 16).Replace("-", "").ToLowerInvariant();
        long[] asm = tab[0x20] != null ? tab[0x20][0] : null; string ver = asm != null ? asm[1] + "." + asm[2] + "." + asm[3] + "." + asm[4] : "-";
        long[][] types = tab[2] ?? new long[0][], meths = tab[6] ?? new long[0][]; List<string> o = new List<string>();
        Func<long, long> blob = i => { long q = st["#Blob"] + i; int c = buf[q]; return q + ((c & 0x80) == 0 ? 1 : (c & 0xc0) == 0x80 ? 2 : 4); };
        Func<long, long> cu = q => { int c = buf[q]; return (c & 0x80) == 0 ? c : (c & 0xc0) == 0x80 ? ((c & 0x3f) << 8) | buf[q + 1] : ((c & 0x1f) << 24) | (buf[q + 1] << 16) | (buf[q + 2] << 8) | buf[q + 3]; };
        for (int ti = 0; ti < types.Length; ti++) {
            long[] ty = types[ti]; string nsn = str(ty[2]); string full = (nsn.Length > 0 ? nsn + "." : "") + str(ty[1]);
            long lo = ty[5] - 1, hi = ti + 1 < types.Length ? types[ti + 1][5] - 1 : meths.Length;
            if (lo < hi) o.Add("t " + full);
            for (long mi = lo; mi < hi && mi < meths.Length; mi++) {
                long[] m = meths[mi]; long sg = blob(m[4]); long prm = cu(sg + 1 + ((buf[sg] & 0x10) != 0 ? 1 : 0));
                long ilSize = 0; uint ilHash = 0; long bo = m[0] != 0 ? off(m[0]) : -1;
                if (bo >= 0) { int hb = buf[bo]; if ((hb & 3) == 2) { ilSize = hb >> 2; ilHash = Fnv32(buf, (int)bo + 1, (int)ilSize); } else { ilSize = U32(buf, bo + 4); ilHash = Fnv32(buf, (int)(bo + 4 * (U16(buf, bo) >> 12)), (int)ilSize); } }
                o.Add("m " + ilSize + " " + H(ilHash >> 8) + " " + prm + " " + str(m[3]));
            }
        }
        o.Insert(0, "A " + str(mod[1]) + " " + mvid + " " + ver + " " + meths.Length);
        return o;
    }
    static byte[] Refpack(byte[] src) {
        int p = 0; int flags = src[p++]; if (src[p++] != 0xfb) throw new Exception("not RefPack");
        int sb = (flags & 0x80) != 0 ? 4 : 3; if ((flags & 1) != 0) p += sb;
        int size = 0; for (int i = 0; i < sb; i++) size = size * 256 + src[p++];
        byte[] o = new byte[size]; int w = 0;
        for (;;) {
            int b0 = src[p++], plain, copy = 0, off = 0;
            if (b0 < 0x80) { int b1 = src[p++]; plain = b0 & 3; copy = ((b0 & 0x1c) >> 2) + 3; off = ((b0 & 0x60) << 3) + b1 + 1; }
            else if (b0 < 0xc0) { int b1 = src[p++], b2 = src[p++]; plain = b1 >> 6; copy = (b0 & 0x3f) + 4; off = ((b1 & 0x3f) << 8) + b2 + 1; }
            else if (b0 < 0xe0) { int b1 = src[p++], b2 = src[p++], b3 = src[p++]; plain = b0 & 3; copy = ((b0 & 0x0c) << 6) + b3 + 5; off = ((b0 & 0x10) << 12) + (b1 << 8) + b2 + 1; }
            else if (b0 < 0xfc) plain = ((b0 & 0x1f) << 2) + 4;
            else { plain = b0 & 3; Array.Copy(src, p, o, w, plain); w += plain; break; }
            Array.Copy(src, p, o, w, plain); w += plain; p += plain;
            for (int i = 0; i < copy; i++, w++) o[w] = o[w - off];
        }
        return o;
    }
    static byte[] DecryptS3SA(byte[] body) {
        int p = 0; int version = body[p++];
        if (version > 1) p += I32(body, p) * 2 + 4;
        p += 4 + 64; int count = U16(body, p); p += 2;
        int tp = p; p += count * 8; int dp = p, tl = count * 8;
        ulong sum = 0; for (int i = 0; i < tl; i += 8) sum += BitConverter.ToUInt64(body, tp + i);
        long seed = (long)((ulong)(tl - 1) & sum);
        byte[] o = new byte[count * 512]; int r = 0;
        for (int i = 0, blk = 0; i < tl; i += 8, blk++) {
            if ((body[tp + i] & 1) != 0) continue;
            for (int j = 0; j < 512; j++, r++) { byte v = body[dp + r]; o[blk * 512 + j] = (byte)(v ^ body[tp + seed]); seed = (seed + v) % tl; }
        }
        return o;
    }
    static byte[] PeImage(byte[] b) {
        for (int i = 0; i + 1 < b.Length; i++) {
            if (b[i] != 0x4d || b[i + 1] != 0x5a) continue;
            if (i + 0x40 > b.Length) break;
            long pe = U32(b, i + 0x3c);
            if (i + pe + 4 <= b.Length && b[i + pe] == 0x50 && b[i + pe + 1] == 0x45 && b[i + pe + 2] == 0 && b[i + pe + 3] == 0) { byte[] o = new byte[b.Length - i]; Array.Copy(b, i, o, 0, o.Length); return o; }
        }
        return null;
    }
    static List<byte[]> S3SA(string file) {
        List<byte[]> o = new List<byte[]>();
        using (FileStream fs = File.OpenRead(file)) {
            Func<long, int, byte[]> rd = (pos, n) => { byte[] x = new byte[n]; fs.Position = pos; int got = 0; while (got < n) { int k = fs.Read(x, got, n - got); if (k <= 0) break; got += k; } return x; };
            byte[] hd = rd(0, 96);
            if (hd[0] != 'D' || hd[1] != 'B' || hd[2] != 'P' || hd[3] != 'F' || U32(hd, 36) == 0) return o;
            long count = U32(hd, 36); byte[] ix = rd(U32(hd, 64), (int)U32(hd, 44)); int p = 0;
            uint flags = U32(ix, p); p += 4;
            uint cT = 0, cG = 0, cH = 0;
            if ((flags & 1) != 0) { cT = U32(ix, p); p += 4; } if ((flags & 2) != 0) { cG = U32(ix, p); p += 4; } if ((flags & 4) != 0) { cH = U32(ix, p); p += 4; }
            for (long i = 0; i < count; i++) {
                uint type = (flags & 1) != 0 ? cT : U32(ix, (p += 4) - 4);
                if ((flags & 2) == 0) p += 4; if ((flags & 4) == 0) p += 4;
                uint offset = U32(ix, p + 4), fsz = U32(ix, p + 8) & 0x7fffffff; int comp = U16(ix, p + 16); p += 20;
                if (type != 0x073faa07) continue;
                byte[] body = rd(offset, (int)fsz); if (comp == 0xffff) body = Refpack(body);
                byte[] img = PeImage(body) ?? PeImage(DecryptS3SA(body)); if (img != null) o.Add(img);
            }
        }
        return o;
    }
    public static List<string> Assemblies(string dir) {
        List<string> o = new List<string>(); string[] fs = Directory.GetFiles(dir); string[] names = Array.ConvertAll(fs, Path.GetFileName); Array.Sort(names, StringComparer.Ordinal);
        foreach (string f in names) {
            string p = Path.Combine(dir, f);
            if (f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) { try { o.AddRange(MethodTable(File.ReadAllBytes(p))); } catch { } }
            else if (f.EndsWith(".package", StringComparison.OrdinalIgnoreCase)) { List<byte[]> imgs; try { imgs = S3SA(p); } catch { continue; } foreach (byte[] x in imgs) { try { o.AddRange(MethodTable(x)); } catch { } } }
        }
        return o;
    }

    // ---- follow-up questions: numbers and printable strings only ----
    public static string Answer(S3Image im, long va, string what, int n) {
        long r = va - im.PrefBase; byte[] b = im.Img;
        if (r < 0 || r >= b.Length) return "outside";
        if (what == "dword") return r + 4 <= b.Length ? H(Norm(im, U32(b, r))) : "outside";
        if (what == "string") { string s = Cstr(b, r, 120); return s ?? "not-text"; }
        if (what == "insn") {
            StringBuilder sb = new StringBuilder(); long p = r; Ins d;
            for (int i = 0; i < Math.Min(n, 64); i++) {
                if (!Decode(b, (int)p, b.Length, out d)) { sb.Append(" invalid"); break; }
                sb.Append(" " + (p - r) + ":" + d.Len + ":op" + H(d.Op));
                if (d.DispSize > 0) sb.Append(":d" + H(UN(b, (int)p + d.DispOff, d.DispSize)));
                if (d.ImmSize > 0) sb.Append(":i" + H(UN(b, (int)p + d.ImmOff, d.ImmSize)));
                if (d.RelSize == 4) sb.Append(":t" + H((uint)(im.PrefBase + p + d.Len + I32(b, p + d.RelOff))));
                p += d.Len;
            }
            return sb.ToString().Trim();
        }
        return "unknown-question";
    }

    // ---- -SelfCheck: synthetic image, synthetic signatures ----
    public static string SelfCheck() {
        byte[] v = new byte[16]; for (int i = 0; i < 16; i++) v[i] = (byte)i;
        if (H(Key48(v, 0)) != "4DB95ECCF470") return "FAILED: hash vector " + H(Key48(v, 0)) + " (tools/port-sigs.ts KEY_HEX 4DB95ECCF470)";
        uint seed = 7; int N = 0x6000; byte[] a = new byte[N];
        for (int i = 0; i < N; i++) { seed = seed * 1103515245 + 12345; a[i] = (byte)((seed >> 16) & 0xff); }
        int[] sites = { 0x300, 0x2100, 0x4800 };
        StringBuilder sig = new StringBuilder("steam - base 400000 cap 256 k 16 core 32\n");
        List<string> tl = new List<string>(), sl = new List<string>(); int ti = 0;
        foreach (int s in sites) { int t0 = ti; for (int t = s - 256; t + 16 <= s + 256; t += 16) { tl.Add(H(0x1000 + t) + " " + H(Key48(a, t))); ti++; } sl.Add(H(0x401000 + s) + " " + t0 + " " + ti + " 0 " + (s == 0x2100 ? 1 : 0)); }
        sig.Append("T " + tl.Count + "\n" + string.Join("\n", tl.ToArray()) + "\nF 1\n" + H(0x1000 + 0x2108) + " 4d2\nS " + sl.Count + "\n" + string.Join("\n", sl.ToArray()) + "\nN 0\n");
        Sig sg = ParseSig(sig.ToString());
        byte[] o = new byte[0x1000 + N + 0x40]; Array.Copy(a, 0, o, 0x1000, 0x1000); Array.Copy(a, 0x1000, o, 0x1000 + 0x1040, N - 0x1000);
        for (int i = 0x4600; i < 0x4a00; i++) o[0x1040 + i] ^= 0x5a;
        o[0x1040 + 0x2108] ^= 0x01;
        S3Image im = new S3Image(); im.Img = o; im.PrefBase = im.RunBase = 0x400000; im.Exec.Add(new int[] { 0x1000, o.Length });
        List<string> r = Match(im, sg);
        string want0 = "S 0 u " + H(0x401300) + " ", want1 = "S 1 u " + H(0x403140) + " ";
        if (!r[0].StartsWith(want0) || !r[0].EndsWith(" -")) return "FAILED: before insert (" + r[0] + ")";
        int i1 = r.FindIndex(x => x.StartsWith("S 1 ")); if (i1 < 0 || !r[i1].StartsWith(want1) || r[i1].EndsWith(" -")) return "FAILED: after insert, one changed tile (" + (i1 < 0 ? "none" : r[i1]) + ")";
        if (!r.Contains("F 1 0 " + H(UN(o, 0x1040 + 0x2108, 4)))) return "FAILED: changed value reported";
        if (!r.Contains("S 2 -") && !r.Exists(x => x.StartsWith("S 2 u ") && !x.EndsWith(" -"))) return "FAILED: destroyed region (" + string.Join(" | ", r.ToArray()) + ")";
        Ins d; byte[] c = { 0x8b, 0x81, 0x18, 0x09, 0, 0 };
        if (!Decode(c, 0, 6, out d) || d.Len != 6 || d.DispSize != 4 || InsClass(c, 0, d.Op) != 0) return "FAILED: decoder";
        return "ok";
    }
}
'@

function Say($t) { Write-Host $t }
function Fail($t) { Write-Host "ERROR: $t" -ForegroundColor Red; Write-Host 'AI needed: none'; exit 1 }

Add-Type -TypeDefinition $cs -Language CSharp

if ($SelfCheck) {
    $r = [S3Port]::SelfCheck()
    Say "Sims3PortProbe self-check: $r"
    Say 'AI needed: none'
    if ($r -ne 'ok') { exit 1 }
    exit 0
}

if (-not $SigFile) { $SigFile = Join-Path $PSScriptRoot 'port-sigs.txt' }
if (-not (Test-Path -LiteralPath $SigFile)) { Fail "port-sigs.txt not found next to this script. Download both files (Sims3PortProbe.ps1 and port-sigs.txt) into the same folder." }
$sigText = [IO.File]::ReadAllText($SigFile)
$sig = [S3Port]::ParseSig($sigText)
$sigId = [S3Port]::SigId($sigText)

$waited = 0
$exePath = $null
if ($ImageFile) {
    $im = [S3Port]::FromFile((Resolve-Path -LiteralPath $ImageFile).Path)
    $exePath = (Resolve-Path -LiteralPath $ImageFile).Path
} else {
    $procs = @(Get-Process -Name 'TS3W', 'TS3' -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { Fail 'The Sims 3 is not running. Start the game, wait for the main menu, then run this again.' }
    $p = $procs[0]
    Say "Found the game (process $($p.ProcessName), id $($p.Id)). Reading only; the game keeps running."
    $h = [S3Port]::Open($p.Id)
    try {
        $exePath = [S3Port]::ExePath($h)
        $base = [S3Port]::ImageBase($h)
        if ($base -eq 0) { try { $base = $p.MainModule.BaseAddress.ToInt64() } catch { $base = 0x400000 } }
        $prefBase = 0
        if ($exePath -and (Test-Path -LiteralPath $exePath)) {
            $fb = [IO.File]::ReadAllBytes($exePath)
            $peo = [S3Port]::U32($fb, 0x3c)
            $prefBase = [S3Port]::U32($fb, $peo + 24 + 28)
        }
        # wait until the code is unpacked: encrypted code has entropy ~8.0, plain x86 code ~6-6.8
        $last = -2.0; $lastChange = Get-Date; $t0 = Get-Date
        while ($true) {
            if ($p.HasExited) { Fail 'The game closed while the probe was waiting. Start it again to the main menu, then rerun.' }
            $e = [S3Port]::CodeEntropy($h, $base)
            $secs = [int]((Get-Date) - $t0).TotalSeconds
            if ($e -ge 0 -and $e -lt 7.2) { Say ("Code unpacked (entropy {0:N2}) after {1} s." -f $e, $secs); break }
            Say ("Waiting for the game to unpack its code: entropy {0:N2} (needs below 7.20), {1} s" -f $e, $secs)
            if ([math]::Abs($e - $last) -gt 0.01) { $last = $e; $lastChange = Get-Date }
            if (((Get-Date) - $lastChange).TotalSeconds -gt $NoProgressSec) { Fail "no progress for $NoProgressSec s (code still looks encrypted, entropy $([math]::Round($e,2))). Make sure the game is at the main menu, then rerun. If it stays like this, tell Rodney this line." }
            Start-Sleep -Seconds 2
        }
        $waited = [int]((Get-Date) - $t0).TotalSeconds
        Say 'Reading the game code from memory...'
        $im = [S3Port]::FromProcess($h, $base, [uint32]$prefBase)
    } finally { [void][S3Port]::CloseHandle($h) }
}

if ($DumpOut) {
    [S3Port]::WritePe($im, $DumpOut)
    Say "DEV ONLY: wrote the unpacked exe image to $DumpOut. It contains game code: never share it. Use: node tools/exe-addr-map.ts --other <that file>"
}

$exeName = if ($exePath) { Split-Path -Leaf $exePath } else { 'unknown.exe' }
$sha = if ($exePath -and (Test-Path -LiteralPath $exePath)) { (Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash.ToLowerInvariant() } else { 'unknown' }
$id8 = $sha.Substring(0, [math]::Min(8, $sha.Length))

if ($Questions) {
    $qs = Get-Content -Raw -LiteralPath $Questions | ConvertFrom-Json
    $out = New-Object System.Collections.Generic.List[string]
    $out.Add('Sims3 port answers v1'); $out.Add("exe: name=$exeName sha256=$sha")
    $i = 0
    foreach ($q in $qs.questions) {
        $va = [Convert]::ToInt64(((([string]$q.va) -replace '^0x', '')), 16)
        $n = if ($q.n) { [int]$q.n } else { 1 }
        $out.Add("Q $i $([string]$q.va) $($q.what) $([S3Port]::Answer($im, $va, [string]$q.what, $n))"); $i++
    }
    if ($ReportOut) { [IO.File]::WriteAllLines($ReportOut, $out); $zip = $ReportOut }
    else {
        $desk = [Environment]::GetFolderPath('Desktop')
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "Sims3PortAnswers-$id8.txt"
        [IO.File]::WriteAllLines($tmp, $out)
        $zip = Join-Path $desk "Sims3PortAnswers-$id8.zip"
        Compress-Archive -LiteralPath $tmp -DestinationPath $zip -Force; Remove-Item -LiteralPath $tmp
    }
    Say "Answered $i questions: $zip"
    Say 'Send that file to Rodney the same way as the report.'
    Say 'AI needed: none'
    exit 0
}

Say 'Matching our fix sites...'
$match = [S3Port]::Match($im, $sig)
Say 'Collecting function fingerprints...'
$fp = [S3Port]::Fingerprints($im)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('Sims3 port report v1 (tools/port-probe/Sims3PortProbe.ps1): addresses, numbers, names and hashes only; no game code.')
$lines.Add("sig: $sigId")
$fv = ''; $pv = ''
if ($exePath -and (Test-Path -LiteralPath $exePath)) { $vi = (Get-Item -LiteralPath $exePath).VersionInfo; $fv = $vi.FileVersion -replace '\s', ''; $pv = $vi.ProductVersion -replace '\s', '' }
$size = if ($exePath -and (Test-Path -LiteralPath $exePath)) { (Get-Item -LiteralPath $exePath).Length } else { 0 }
$lines.Add("exe: name=$exeName size=$size sha256=$sha fileversion=$fv productversion=$pv")
$img = $im.Img
$pe = [S3Port]::U32($img, 0x3c)
$secLines = [S3Port]::SectionLines($im)
$ooa = if (($secLines | Where-Object { $_ -match 'name=\.ooa ' }).Count -gt 0) { 'yes' } else { 'no' }
$lines.Add(('pe: timestamp=0x{0:X} imagebase=0x{1:X} runbase=0x{2:X} sizeofimage=0x{3:X} entry=0x{4:X} ooa={5}' -f [S3Port]::U32($img, $pe + 8), $im.PrefBase, $im.RunBase, $img.Length, [S3Port]::U32($img, $pe + 24 + 16), $ooa))
foreach ($l in $secLines) { $lines.Add($l) }
$lines.Add("dump: unreadable-pages=$($im.Unreadable) waited-s=$waited code-sha256=$([S3Port]::CodeSha($im))")

# install context: names and versions only
$binDir = if ($exePath) { Split-Path -Parent $exePath } else { $null }
if ($binDir -and -not $ImageFile) {
    $gameDir = Split-Path -Parent $binDir; $root = Split-Path -Parent $gameDir
    $sku = Join-Path $binDir 'skuversion.txt'
    if (Test-Path -LiteralPath $sku) { foreach ($l in (Get-Content -LiteralPath $sku | Where-Object { $_ -match 'version' } | Select-Object -First 6)) { $lines.Add("game: $($l.Trim() -replace '[\\/:][^ ]*\\', '')") } }
    $packs = @()
    foreach ($d in @($root, (Split-Path -Parent $root))) { if ($d -and (Test-Path -LiteralPath $d)) { $packs += @(Get-ChildItem -LiteralPath $d -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(EP|SP)\d+$' } | ForEach-Object { $_.Name }) } }
    $lines.Add("packs: folders=$((($packs | Sort-Object -Unique) -join ',')) registry=$(((Get-ChildItem 'HKLM:\SOFTWARE\WOW6432Node\Sims' -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName }) -join ';'))")
    $addons = @(Get-ChildItem -LiteralPath $binDir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\.asi$' -or $_.Name -match '^(d3d9|dinput8|dsound|version|winmm|dxgi|ddraw|ts3native|ts3fixes)\.dll$' -or $_.Name -match '\.(ini|toml)$' -and $_.Name -match 'S3SS|Sims3Performance|apex' } | ForEach-Object { $_.Name })
    $lines.Add("bin: $($addons -join ',')")
    if (-not $AssemblyDir) { $AssemblyDir = Join-Path $binDir 'Gameplay' }
}
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
if ($cv) { $lines.Add("windows: $($cv.ProductName) $($cv.DisplayVersion) build $($cv.CurrentBuild)") }

foreach ($l in $match) { $lines.Add($l) }
foreach ($l in $fp) { $lines.Add($l) }
if ($AssemblyDir -and (Test-Path -LiteralPath $AssemblyDir)) {
    Say 'Reading script assembly method tables...'
    foreach ($l in [S3Port]::Assemblies($AssemblyDir)) { $lines.Add($l) }
} else { $lines.Add('assemblies: not found') }

# scrub: user, machine and profile path never leave the PC
$scrub = @($env:USERPROFILE, $env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -ge 3 }
for ($i = 0; $i -lt $lines.Count; $i++) { foreach ($s in $scrub) { if ($lines[$i].IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $lines[$i] = $lines[$i] -ireplace [regex]::Escape($s), '<scrubbed>' } } }

$summary = ($match | Where-Object { $_ -like 'summary:*' }) -join ''
if ($ReportOut) {
    [IO.File]::WriteAllLines($ReportOut, $lines)
    Say "Report: $ReportOut ($summary)"
} else {
    $desk = [Environment]::GetFolderPath('Desktop')
    $tmpDir = Join-Path ([IO.Path]::GetTempPath()) ("s3port-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmpDir | Out-Null
    $tmp = Join-Path $tmpDir "Sims3PortReport-$id8.txt"
    [IO.File]::WriteAllLines($tmp, $lines)
    $zip = Join-Path $desk "Sims3PortReport-$id8.zip"
    Compress-Archive -LiteralPath $tmp -DestinationPath $zip -Force
    Remove-Item -LiteralPath $tmpDir -Recurse -Force
    Say ''
    Say "Done ($summary). Your report: $zip ($([math]::Round((Get-Item -LiteralPath $zip).Length / 1KB)) KB)"
    Say 'Send it to Rodney: open https://github.com/rodneypantonial/sims3-modernization/issues/new?template=port-report.yml and drag the zip into the form, or send it in a Discord DM.'
    Say 'It holds only numbers, names and hashes about the game files: no game code, no paths, no user or machine name.'
}
Say 'AI needed: none'
