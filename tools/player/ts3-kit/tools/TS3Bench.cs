// Kit benchmark mode (docs/315): the runner's process-memory client for the ts3agent mailbox (mods/ts3agent/src/loader/Mailbox.cs),
// the game's frame counter and the native start-up dialog. Loaded by TS3Bench.ps1 with Add-Type (Windows PowerShell 5.1 = C# 5,
// PowerShell 7 = Roslyn), so plain C# 5 only. No network: the channel is ReadProcessMemory / WriteProcessMemory on one PID.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Ts3Bench
{
    public class Mbox : IDisposable
    {
        // layout must match Mailbox.cs and tools/probe/agent.js MBX
        public const int Header = 64, ReqCap = 64 * 1024, Capacity = 1024 * 1024, Version = 1;
        public const uint Salt = 0x53334D43;
        const int OffNonce = 20, OffReqSeq = 24, OffRespSeq = 28, OffReqLen = 32, OffRespLen = 36, OffCapacity = 40,
                  OffCheck = 44, OffHeartbeat = 48, OffState = 56;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("TS3AGENT-MBOX-01");

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr done);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr done);
        [DllImport("kernel32.dll")] static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out Mbi info, IntPtr len);

        [StructLayout(LayoutKind.Sequential)]
        struct Mbi { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect; public IntPtr RegionSize; public uint State, Protect, Type; }

        IntPtr mH;
        public long Addr;
        public long LastScanMs;

        public Mbox(int pid)
        {
            // VM_READ | VM_WRITE | VM_OPERATION | QUERY_INFORMATION
            mH = OpenProcess(0x0010 | 0x0020 | 0x0008 | 0x0400, false, pid);
            if (mH == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException("OpenProcess " + pid + " failed: " + err + (err == 5 ? " (access denied: an antivirus or the game running as administrator; see docs/315 section 8)" : ""));
            }
        }

        public void Dispose() { if (mH != IntPtr.Zero) { CloseHandle(mH); mH = IntPtr.Zero; } }

        public byte[] Read(long addr, int n)
        {
            byte[] b = new byte[n];
            IntPtr done;
            if (!ReadProcessMemory(mH, new IntPtr(addr), b, new IntPtr(n), out done) || done.ToInt64() != n) return null;
            return b;
        }

        bool Write(long addr, byte[] b)
        {
            IntPtr done;
            return WriteProcessMemory(mH, new IntPtr(addr), b, new IntPtr(b.Length), out done) && done.ToInt64() == b.Length;
        }

        // u32 at an absolute address (frame counter) or -1 when unreadable
        public long U32At(long addr) { byte[] b = Read(addr, 4); return b == null ? -1 : (long)BitConverter.ToUInt32(b, 0); }
        uint U32(int off) { byte[] b = Read(Addr + off, 4); if (b == null) throw new InvalidOperationException("mailbox unreadable"); return BitConverter.ToUInt32(b, 0); }
        void PutU32(int off, uint v) { if (!Write(Addr + off, BitConverter.GetBytes(v))) throw new InvalidOperationException("mailbox write failed"); }

        public static bool HeaderValid(byte[] h)
        {
            if (h == null || h.Length < Header) return false;
            for (int i = 0; i < Magic.Length; i++) if (h[i] != Magic[i]) return false;
            uint ver = BitConverter.ToUInt32(h, 16), nonce = BitConverter.ToUInt32(h, OffNonce), cap = BitConverter.ToUInt32(h, OffCapacity);
            return ver == Version && cap == Capacity && ((uint)Version ^ nonce ^ cap ^ Salt) == BitConverter.ToUInt32(h, OffCheck);
        }

        public bool Valid() { return Addr != 0 && HeaderValid(Read(Addr, Header)); }

        // Committed read-write regions of at least 1 MB (the buffer is one 1 MB byte[]), read in 4 MB chunks.
        public bool Scan()
        {
            if (Valid()) return true;
            Addr = 0;
            long t0 = Environment.TickCount;
            long a = 0x10000;
            const int Chunk = 4 << 20;
            while (a < 0x100000000L)
            {
                Mbi m;
                if (VirtualQueryEx(mH, new IntPtr(a), out m, new IntPtr(Marshal.SizeOf(typeof(Mbi)))) == IntPtr.Zero) break;
                long baseA = m.BaseAddress.ToInt64(), size = m.RegionSize.ToInt64();
                if (size <= 0) break;
                // READWRITE or EXECUTE_READWRITE: the game's Boehm heap is RWX (Frida's 'rw-' in tools/probe/agent.js matches both)
                uint p = m.Protect & 0xFF;
                if (m.State == 0x1000 && (p == 0x04 || p == 0x40) && (m.Protect & 0x100) == 0 && size >= Capacity)
                {
                    for (long off = 0; off < size; off += Chunk - Header)
                    {
                        int n = (int)Math.Min(Chunk, size - off);
                        byte[] buf = Read(baseA + off, n);
                        if (buf == null) continue;
                        for (int i = 0; i + Header <= n; i++)
                        {
                            if (buf[i] != Magic[0] || buf[i + 1] != Magic[1]) continue;
                            byte[] h = new byte[Header];
                            Buffer.BlockCopy(buf, i, h, 0, Header);
                            if (HeaderValid(h)) { Addr = baseA + off + i; LastScanMs = Environment.TickCount - t0; return true; }
                        }
                    }
                }
                a = baseA + size;
            }
            LastScanMs = Environment.TickCount - t0;
            return false;
        }

        public bool Pending() { return U32(OffReqSeq) != U32(OffRespSeq); }
        public uint Heartbeat() { return U32(OffHeartbeat); }
        public bool WorldLoaded() { return U32(OffState) == 1; }

        public uint Send(string json)
        {
            if (Pending()) throw new InvalidOperationException("previous request still pending");
            byte[] data = Encoding.UTF8.GetBytes(json);
            if (data.Length > ReqCap) throw new InvalidOperationException("request too large");
            if (!Write(Addr + Header, data)) throw new InvalidOperationException("mailbox write failed");
            PutU32(OffReqLen, (uint)data.Length);
            uint seq = U32(OffReqSeq) + 1;
            PutU32(OffReqSeq, seq);  // publish last; the game answers on its next sim tick
            return seq;
        }

        public string Reply(uint seq)
        {
            if (U32(OffRespSeq) != seq) return null;
            int len = (int)U32(OffRespLen);
            if (len < 0 || len > Capacity - Header - ReqCap) throw new InvalidOperationException("bad reply length " + len);
            byte[] b = Read(Addr + Header + ReqCap, len);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }
    }

    public static class Win
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        // the exe's mod-version warning (docs/231): template 104, buttons 1001 Quit / 1004 Continue, static 1003
        public static bool IsModVersionDialog(string cls, bool has1001, bool has1003, bool has1004)
        {
            return cls == "#32770" && has1001 && has1003 && has1004;
        }

        // Posts WM_COMMAND(BN_CLICKED, Continue) to that dialog of this PID (what the button itself sends; no focus change).
        // Returns the dialog text, or null when there is none.
        public static string AnswerModVersionDialog(int pid)
        {
            string hit = null;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                int p;
                GetWindowThreadProcessId(h, out p);
                if (p != pid || !IsWindowVisible(h)) return true;
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                IntPtr cont = GetDlgItem(h, 1004);
                if (!IsModVersionDialog(sb.ToString(), GetDlgItem(h, 1001) != IntPtr.Zero, GetDlgItem(h, 1003) != IntPtr.Zero, cont != IntPtr.Zero)) return true;
                StringBuilder t = new StringBuilder(1024);
                GetWindowText(GetDlgItem(h, 1003), t, 1024);
                if (PostMessage(h, 0x0111, new IntPtr(1004), cont)) hit = t.ToString();
                return hit == null;
            }, IntPtr.Zero);
            return hit;
        }

        public static bool Foreground(int pid)
        {
            int p;
            GetWindowThreadProcessId(GetForegroundWindow(), out p);
            return p == pid;
        }
    }
}
