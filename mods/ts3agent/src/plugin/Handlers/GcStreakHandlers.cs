using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ts3Agent
{
    // docs/67 §35 repro (repros.json gc-streak-guard). STRESS, not a player flow: forces the state of the bp-jit-autshadow-r crash
    // instead of waiting for it (1 of 233 runs).
    public static partial class Handlers
    {
        // Steam 1.67 [verified: GC_is_black_listed 0xE4A560, GC_promote_black_lists 0xE4A7C0]
        const int kGcOldStackBl = 0x01234354, kGcBlSpacing = 0x0119482C, kGcHeapSize = 0x012225A0;
        const int kGcPuntWarn = 0x00E49540;  // GcBlackOk's call (E8) when [GC] BlackListCapKB is on, else FF 15
        const int kGcBlHashWords = 0x800;    // 65,536-bit page hash (addr >> 12) & 0xFFFF

        // gc_streak_force {rounds?: 2, chars?: 421870, spacing_kb?: 1024}, per round: a collection (a new gc_no, so a new streak),
        // then with nothing allocated in between one page in 128 black-listed and the spacing at the cap, then one string of
        // 206 blocks (824 KB). Every free span of 128+ pages now holds a black-listed page and the request is below the spacing, so
        // GC_allochblk_nth refuses it in every section and GC_alloc_large expands until ts3native's GwExpand drops the spacing
        // (native_set gcstreakat: 3 = the guard, 12 = guard off, test stop). Requests under 512 KB still find clean pages.
        // Bits and spacing go back after each round. Returns heap KB before / after per round.
        static object GcStreakForce(Args a)
        {
            RequireLive();
            int rounds = Math.Max(1, (int)a.Long("rounds", 2)), chars = (int)a.Long("chars", 421870);
            int spacing = (int)a.Long("spacing_kb", 1024) << 10;
            // without the cap GwExpand has no guard and no stop: the expand loop would run out of address space
            if (Marshal.ReadByte(new IntPtr(kGcPuntWarn)) != 0xE8) throw new McpException("[GC] BlackListCapKB is off or not the Steam 1.67 exe");
            int[] saved = new int[kGcBlHashWords / 4];
            List<object> rows = new List<object>();
            for (int i = 0; i < rounds; i++)
            {
                GC.Collect();
                IntPtr bl = Marshal.ReadIntPtr(new IntPtr(kGcOldStackBl));
                int oldSpacing = Marshal.ReadInt32(new IntPtr(kGcBlSpacing)), before = Marshal.ReadInt32(new IntPtr(kGcHeapSize));
                for (int w = 0; w < saved.Length; w++)
                {
                    IntPtr p = new IntPtr(bl.ToInt32() + w * 16);
                    saved[w] = Marshal.ReadInt32(p);
                    Marshal.WriteInt32(p, saved[w] | 1);
                }
                Marshal.WriteInt32(new IntPtr(kGcBlSpacing), spacing);
                string s = new string('x', chars);
                int after = Marshal.ReadInt32(new IntPtr(kGcHeapSize));
                for (int w = 0; w < saved.Length; w++) Marshal.WriteInt32(new IntPtr(bl.ToInt32() + w * 16), saved[w]);
                Marshal.WriteInt32(new IntPtr(kGcBlSpacing), oldSpacing);
                Dictionary<string, object> r = Util.Obj();
                r["length"] = s.Length;
                r["spacing_kb_before"] = oldSpacing >> 10;
                r["heap_kb_before"] = (int)((uint)before >> 10);
                r["heap_kb_after"] = (int)((uint)after >> 10);
                rows.Add(r);
            }
            return rows;
        }
    }
}
