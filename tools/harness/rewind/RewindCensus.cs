// Read-only process census for the rewind feasibility check (docs/ rewind design, step 1). Loaded by rewind-census.ts with
// Add-Type in a 64-bit PowerShell. Rights used: PROCESS_QUERY_INFORMATION | PROCESS_VM_READ on the process,
// THREAD_QUERY_INFORMATION on its threads; plus Toolhelp, VirtualQueryEx, ReadProcessMemory and NtQuerySystemInformation.
// Nothing here suspends, writes, injects or debugs.
// C# 5 only (Windows PowerShell 5.1 can compile it too).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class RewindCensus
{
    const uint PROCESS_QUERY_INFORMATION = 0x400, PROCESS_VM_READ = 0x10, THREAD_QUERY_INFORMATION = 0x40;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_FREE = 0x10000;
    const uint PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    const int PAGE = 4096;

    [StructLayout(LayoutKind.Sequential)]
    struct MBI { public ulong BaseAddress, AllocationBase; public uint AllocationProtect, __a; public ulong RegionSize; public uint State, Protect, Type, __b; }
    [StructLayout(LayoutKind.Sequential)]
    struct THREADENTRY32 { public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID; public int tpBasePri, tpDeltaPri; public uint dwFlags; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint a, bool i, uint tid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool IsWow64Process(IntPtr h, out bool w);
    [DllImport("kernel32.dll")] static extern bool GetProcessHandleCount(IntPtr h, out uint n);
    [DllImport("kernel32.dll")] static extern UIntPtr VirtualQueryEx(IntPtr h, UIntPtr a, out MBI m, UIntPtr len);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, UIntPtr a, byte[] buf, UIntPtr n, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
    [DllImport("kernel32.dll")] static extern bool Thread32First(IntPtr s, ref THREADENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool Thread32Next(IntPtr s, ref THREADENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr h, out long c, out long e, out long k, out long u);
    [DllImport("psapi.dll", CharSet = CharSet.Unicode)] static extern uint GetMappedFileNameW(IntPtr h, UIntPtr a, StringBuilder s, uint n);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr h, int c, out ulong v, int len, IntPtr ret);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int c, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryObject(IntPtr h, int c, IntPtr buf, int len, out int ret);

    static string Hex(ulong v) { return "\"0x" + v.ToString("X") + "\""; }
    static string Js(string s) { return s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }
    static bool Writable(uint p) { p &= 0xFF; return p == 0x04 || p == 0x08 || p == 0x40 || p == 0x80; }

    // type index -> name, from NtQueryObject(ObjectTypesInformation); OBJECT_TYPE_INFORMATION is 0x68 bytes on x64,
    // TypeIndex at 0x5A, the name buffer follows the struct (pointer aligned)
    public static Dictionary<int, string> TypeNames()
    {
        var d = new Dictionary<int, string>();
        int len = 1 << 16, ret;
        for (int tries = 0; tries < 8; tries++, len *= 2)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int st = NtQueryObject(IntPtr.Zero, 3, buf, len, out ret);
                if (st == unchecked((int)0xC0000004)) continue;
                if (st != 0) return d;
                int n = Marshal.ReadInt32(buf);
                long off = 8;
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = new IntPtr(buf.ToInt64() + off);
                    int l = Marshal.ReadInt16(e) & 0xFFFF, max = Marshal.ReadInt16(e, 2) & 0xFFFF;
                    IntPtr s = Marshal.ReadIntPtr(e, 8);
                    int idx = Marshal.ReadByte(e, 0x5A);
                    d[idx] = Marshal.PtrToStringUni(s, l / 2);
                    off = (off + 0x68 + max + 7) & ~7L;
                }
                return d;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return d;
    }

    // ObjectTypeIndex of every handle the process holds, from SystemExtendedHandleInformation (64); entries are 40 bytes on x64
    public static List<ulong[]> Handles(uint pid, out bool ok)
    {
        var r = new List<ulong[]>();
        ok = false;
        int len = 1 << 22, ret;
        for (int tries = 0; tries < 8; tries++)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int st = NtQuerySystemInformation(64, buf, len, out ret);
                if (st == unchecked((int)0xC0000004)) { len = Math.Max(len * 2, ret + (1 << 20)); continue; }
                if (st != 0) return r;
                long n = Marshal.ReadInt64(buf);
                for (long i = 0; i < n; i++)
                {
                    IntPtr e = new IntPtr(buf.ToInt64() + 16 + i * 40);
                    if ((ulong)Marshal.ReadInt64(e, 8) != pid) continue;
                    r.Add(new ulong[] { (ulong)Marshal.ReadInt64(e, 16), (ulong)(Marshal.ReadInt16(e, 30) & 0xFFFF), (uint)Marshal.ReadInt32(e, 24) });
                }
                ok = true;
                return r;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return r;
    }

    // FNV-1a over 64-bit words, one value per 4 KB page; 0 = page not readable
    static ulong HashPage(ulong[] w, int start)
    {
        ulong h = 14695981039346656037UL;
        for (int i = start, e = start + PAGE / 8; i < e; i++) { h ^= w[i]; h *= 1099511628211UL; }
        return h == 0 ? 1 : h;
    }

    public static string Snap(uint pid, bool hash, string outPath)
    {
        var t0 = DateTime.UtcNow;
        IntPtr hp = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (hp == IntPtr.Zero) throw new Exception("OpenProcess " + pid + " error " + Marshal.GetLastWin32Error());
        var sb = new StringBuilder();
        try
        {
            bool wow; IsWow64Process(hp, out wow);
            uint hc; GetProcessHandleCount(hp, out hc);
            sb.Append("{\"pid\":").Append(pid).Append(",\"wow64\":").Append(wow ? "true" : "false").Append(",\"utc\":").Append(Js(t0.ToString("o")))
              .Append(",\"handleCount\":").Append(hc);

            // regions
            var mapped = new Dictionary<ulong, string>();
            var rsb = new StringBuilder();
            var hsb = new StringBuilder();
            ulong a = 0, priv = 0, img = 0, map = 0, wr = 0, priv32 = 0; int nreg = 0; long hashedPages = 0, unreadPages = 0;
            byte[] chunk = new byte[1 << 20]; ulong[] words = new ulong[(1 << 20) / 8];
            MBI m;
            while (VirtualQueryEx(hp, new UIntPtr(a), out m, new UIntPtr((uint)Marshal.SizeOf(typeof(MBI)))) != UIntPtr.Zero)
            {
                if (m.RegionSize == 0) break;
                if (m.State != MEM_FREE)
                {
                    string name = null;
                    if (m.Type != 0x20000 && !mapped.TryGetValue(m.AllocationBase, out name))
                    {
                        var s = new StringBuilder(520);
                        name = GetMappedFileNameW(hp, new UIntPtr(m.AllocationBase), s, 520) > 0 ? s.ToString() : null;
                        if (name != null) { int k = name.LastIndexOf('\\'); if (k >= 0) name = name.Substring(k + 1); }
                        mapped[m.AllocationBase] = name;
                    }
                    if (rsb.Length > 0) rsb.Append(',');
                    rsb.Append('[').Append(Hex(m.BaseAddress)).Append(',').Append(m.RegionSize).Append(',').Append(Hex(m.AllocationBase)).Append(',')
                       .Append(m.State == MEM_COMMIT ? "\"C\"" : "\"R\"").Append(',').Append(m.Protect).Append(',')
                       .Append(m.Type == 0x20000 ? "\"P\"" : m.Type == 0x40000 ? "\"M\"" : "\"I\"").Append(',').Append(Js(name)).Append(']');
                    nreg++;
                    if (m.State == MEM_COMMIT)
                    {
                        if (m.Type == 0x20000) { priv += m.RegionSize; if (m.BaseAddress < 0x100000000UL) priv32 += m.RegionSize; }
                        else if (m.Type == 0x40000) map += m.RegionSize; else img += m.RegionSize;
                        bool w = Writable(m.Protect) && (m.Protect & PAGE_GUARD) == 0;
                        if (w) wr += m.RegionSize;
                        if (w && hash)
                        {
                            // per-page hashes of this region, base64 of the ulong array
                            ulong pages = m.RegionSize / PAGE;
                            var hv = new ulong[pages];
                            for (ulong off = 0; off < m.RegionSize; off += (ulong)chunk.Length)
                            {
                                ulong n = Math.Min((ulong)chunk.Length, m.RegionSize - off);
                                UIntPtr got;
                                bool okr = ReadProcessMemory(hp, new UIntPtr(m.BaseAddress + off), chunk, new UIntPtr(n), out got);
                                if (okr && got.ToUInt64() == n)
                                {
                                    Buffer.BlockCopy(chunk, 0, words, 0, (int)n);
                                    for (ulong p = 0; p < n / PAGE; p++) hv[(off / PAGE) + p] = HashPage(words, (int)(p * PAGE / 8));
                                    hashedPages += (long)(n / PAGE);
                                }
                                else
                                {
                                    // chunk failed (page decommitted meanwhile): retry page by page
                                    for (ulong p = 0; p < n / PAGE; p++)
                                    {
                                        byte[] one = new byte[PAGE];
                                        if (ReadProcessMemory(hp, new UIntPtr(m.BaseAddress + off + p * PAGE), one, new UIntPtr(PAGE), out got) && got.ToUInt64() == PAGE)
                                        { Buffer.BlockCopy(one, 0, words, 0, PAGE); hv[(off / PAGE) + p] = HashPage(words, 0); hashedPages++; }
                                        else unreadPages++;
                                    }
                                }
                            }
                            byte[] hb = new byte[pages * 8]; Buffer.BlockCopy(hv, 0, hb, 0, hb.Length);
                            if (hsb.Length > 0) hsb.Append(',');
                            hsb.Append(Js("0x" + m.BaseAddress.ToString("X"))).Append(':').Append(Js(Convert.ToBase64String(hb)));
                        }
                    }
                }
                ulong next = m.BaseAddress + m.RegionSize;
                if (next <= a) break;
                a = next;
            }
            sb.Append(",\"totals\":{\"private\":").Append(priv).Append(",\"private32\":").Append(priv32).Append(",\"image\":").Append(img)
              .Append(",\"mapped\":").Append(map).Append(",\"writable\":").Append(wr).Append(",\"regions\":").Append(nreg)
              .Append(",\"hashedPages\":").Append(hashedPages).Append(",\"unreadPages\":").Append(unreadPages).Append('}');

            // threads
            var tsb = new StringBuilder();
            IntPtr snap = CreateToolhelp32Snapshot(4, 0);
            var te = new THREADENTRY32(); te.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));
            int nth = 0;
            if (Thread32First(snap, ref te)) do
            {
                if (te.th32OwnerProcessID != pid) continue;
                ulong start = 0; long ct = 0, ex, k, u;
                IntPtr ht = OpenThread(THREAD_QUERY_INFORMATION, false, te.th32ThreadID);
                if (ht != IntPtr.Zero) { NtQueryInformationThread(ht, 9, out start, 8, IntPtr.Zero); GetThreadTimes(ht, out ct, out ex, out k, out u); CloseHandle(ht); }
                if (tsb.Length > 0) tsb.Append(',');
                tsb.Append('[').Append(te.th32ThreadID).Append(',').Append(Hex(start)).Append(',').Append(ct).Append(']');
                nth++;
            } while (Thread32Next(snap, ref te));
            CloseHandle(snap);

            // handles
            bool hok; var hl = Handles(pid, out hok);
            var tn = TypeNames();
            var hhsb = new StringBuilder();
            foreach (var h in hl)
            {
                string n; if (!tn.TryGetValue((int)h[1], out n)) n = "#" + h[1];
                if (hhsb.Length > 0) hhsb.Append(',');
                hhsb.Append('[').Append(h[0]).Append(',').Append(Js(n)).Append(',').Append(h[2]).Append(']');
            }
            sb.Append(",\"handlesOk\":").Append(hok ? "true" : "false").Append(",\"handles\":[").Append(hhsb).Append(']')
              .Append(",\"threads\":[").Append(tsb).Append(']')
              .Append(",\"regions\":[").Append(rsb).Append(']')
              .Append(",\"hashes\":{").Append(hsb).Append('}')
              .Append(",\"ms\":").Append((long)(DateTime.UtcNow - t0).TotalMilliseconds).Append('}');
        }
        finally { CloseHandle(hp); }
        File.WriteAllText(outPath, sb.ToString());
        return outPath;
    }
}
