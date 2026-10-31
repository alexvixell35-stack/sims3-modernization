// Round 6 B4 (docs/207 §10): differential test corpus for the monohost method compiler (P0 now, P1 next). Every case runs
// on EA's interpreter twice and once compiled; the hashes of results and caught exceptions (type + the throwing frame's
// line, which carries the IL offset) must be equal. Edge cases on purpose: div / rem with INT_MIN / -1 and zero,
// mul / add / sub / conv .ovf, shifts past the width, x87-sensitive float math (NaN payloads, signaling NaN, denormals,
// -0, 53- vs 64-bit rounding, float stores), EA's superinstructions 0x1AB-0x1C0 with their fault paths, value types, casts,
// arrays, switch, try / catch / finally, and the BCL / SimIFace methods P0 compiles (Dictionary with colliding keys,
// List growth, Array.Resize / IndexOf, Enum, Math, ObjectGuid, CASUtils).
//   node tools/jit-corpus.ts --write                write native/monohost/JitCorpus.cs (build.cmd compiles it into MonoHostTest.dll)
//   node tools/jit-corpus.ts --env                  print MONOHOST_JITLIST: P0's default list (kJitList) + the corpus kernels
//   node tools/jit-corpus.ts --report <monohost.exe.log> [--dump <jit-dump.txt>]   summary, mismatches by case name, superop coverage
//   node tools/jit-corpus.ts --self-check
// Run (docs/207 §10): MONOHOST_JIT=verify MONOHOST_MINTCALL=fast MONOHOST_JITCORPUS=1 MONOHOST_DIVGUARD=1 MONOHOST_JITLIST=<--env>
//   [MONOHOST_JITCORPUS_FROM=<first case> MONOHOST_JITCORPUS_TO=<last case>]
//   monohost.exe <repo>\game-copy\monohost\TS3W.exe <repo>\game-copy\assemblies selftest <repo>\game-copy\monohost\hostdata.txt
// Yields are out of scope: P0 compiles only never-yielding methods, and monohost runs no task scheduler (P1 adds them).
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const OUT = resolve(ROOT, 'native/monohost/JitCorpus.cs');
const arg = (n: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : undefined; };

// ---- inputs ----
const I4 = ['0', '1', '-1', '2', '-2', '3', '7', '-7', '16', '31', '32', '33', '0x7FFF', '0x8000', '0xFFFF', '0x10000', '0x3FFFFFFF',
  '0x40000000', 'int.MaxValue', 'int.MinValue', 'int.MinValue + 1', 'int.MaxValue - 1'];
const I8 = ['0L', '1L', '-1L', '2L', '-2L', '7L', '0x7FFFFFFFL', '0x80000000L', '-0x80000000L', '0xFFFFFFFFL', '0x100000000L',
  'long.MaxValue', 'long.MinValue', 'long.MinValue + 1', '-3037000500L', '3037000500L'];
const SH = ['0', '1', '7', '15', '16', '31', '32', '33', '63', '64', '65', '-1', 'int.MinValue'];
// doubles as bit patterns (exact, incl. NaN payloads), then decimal values around conversion and rounding edges
const R8 = ['0x0000000000000000', '0x8000000000000000', '0x3FF0000000000000', '0xBFF0000000000000', '0x3FB999999999999A',
  '0x7FEFFFFFFFFFFFFF', '0x0000000000000001', '0x000FFFFFFFFFFFFF', '0x0010000000000000', '0x7FF0000000000000',
  '0xFFF0000000000000', '0x7FF8000000000000', '0xFFF8000000000000', '0x7FF0000000000001', '0x7FF8000000000123',
  '0x3FF0000000000001', '0x3FEFFFFFFFFFFFFF', '0x4340000000000001'].map(b => `D(0x${b.slice(2)}UL)`).concat(
  ['0.5', '1.5', '2.5', '-0.5', '-1.5', '3.0', '1e16', '2147483647.5', '-2147483648.5', '2147483648.0', '-2147483649.0',
    '4294967295.5', '4294967296.0', '9.2233720368547758E18', '-9.2233720368547758E18', '1.8446744073709552E19']);
const R4 = ['0x00000000', '0x80000000', '0x3F800000', '0xBF800000', '0x3DCCCCCD', '0x7F7FFFFF', '0x00000001', '0x007FFFFF',
  '0x00800000', '0x7F800000', '0xFF800000', '0x7FC00000', '0xFFC00000', '0x7F800001', '0x4F000000', '0xCF000000', '0x4F800000',
  '0x3F000000', '0x3FC00000', '0x40200000', '0x33800000', '0x3F800001'].map(b => `F(${b}U)`);

// ---- kernels: static methods of class JitK, compiled through "JitK.*" ----
type Kind = 'i4i4' | 'u4u4' | 'i8i8' | 'u8u8' | 'i4' | 'u4' | 'i8' | 'u8' | 'i4sh' | 'u4sh' | 'i8sh' | 'u8sh' | 'r8r8' | 'r8' | 'r4r4' | 'r4';
const P: Record<Kind, [string, string, string]> = {  // C# parameter list, input array(s), argument expression
  i4i4: ['int a, int b', 'I4,I4', 'I4[i], I4[j]'], u4u4: ['uint a, uint b', 'I4,I4', '(uint)I4[i], (uint)I4[j]'],
  i8i8: ['long a, long b', 'I8,I8', 'I8[i], I8[j]'], u8u8: ['ulong a, ulong b', 'I8,I8', '(ulong)I8[i], (ulong)I8[j]'],
  i4: ['int a', 'I4', 'I4[i]'], u4: ['uint a', 'I4', '(uint)I4[i]'], i8: ['long a', 'I8', 'I8[i]'], u8: ['ulong a', 'I8', '(ulong)I8[i]'],
  i4sh: ['int a, int b', 'I4,SH', 'I4[i], SH[j]'], u4sh: ['uint a, int b', 'I4,SH', '(uint)I4[i], SH[j]'],
  i8sh: ['long a, int b', 'I8,SH', 'I8[i], SH[j]'], u8sh: ['ulong a, int b', 'I8,SH', '(ulong)I8[i], SH[j]'],
  r8r8: ['double a, double b', 'R8,R8', 'R8[i], R8[j]'], r8: ['double a', 'R8', 'R8[i]'],
  r4r4: ['float a, float b', 'R4,R4', 'R4[i], R4[j]'], r4: ['float a', 'R4', 'R4[i]'],
};
// [name, kind, return type, body expression]
const K: [string, Kind, string, string][] = [
  ['DivI4', 'i4i4', 'int', 'a / b'], ['RemI4', 'i4i4', 'int', 'a % b'], ['DivUnI4', 'u4u4', 'uint', 'a / b'], ['RemUnI4', 'u4u4', 'uint', 'a % b'],
  ['MulI4', 'i4i4', 'int', 'unchecked(a * b)'], ['MulOvfI4', 'i4i4', 'int', 'checked(a * b)'], ['MulOvfUnI4', 'u4u4', 'uint', 'checked(a * b)'],
  ['AddOvfI4', 'i4i4', 'int', 'checked(a + b)'], ['SubOvfI4', 'i4i4', 'int', 'checked(a - b)'], ['AddOvfUnI4', 'u4u4', 'uint', 'checked(a + b)'],
  ['SubOvfUnI4', 'u4u4', 'uint', 'checked(a - b)'], ['AndOrXorI4', 'i4i4', 'int', '(a & b) ^ (a | ~b)'],
  ['DivI8', 'i8i8', 'long', 'a / b'], ['RemI8', 'i8i8', 'long', 'a % b'], ['DivUnI8', 'u8u8', 'ulong', 'a / b'], ['RemUnI8', 'u8u8', 'ulong', 'a % b'],
  ['MulI8', 'i8i8', 'long', 'unchecked(a * b)'], ['MulOvfI8', 'i8i8', 'long', 'checked(a * b)'], ['MulOvfUnI8', 'u8u8', 'ulong', 'checked(a * b)'],
  ['AddOvfI8', 'i8i8', 'long', 'checked(a + b)'], ['SubOvfI8', 'i8i8', 'long', 'checked(a - b)'],
  ['ShlI4', 'i4sh', 'int', 'a << b'], ['ShrI4', 'i4sh', 'int', 'a >> b'], ['ShrUnI4', 'u4sh', 'uint', 'a >> b'],
  ['ShlI8', 'i8sh', 'long', 'a << b'], ['ShrI8', 'i8sh', 'long', 'a >> b'], ['ShrUnI8', 'u8sh', 'ulong', 'a >> b'],
  ['NegNotI4', 'i4', 'int', '-a ^ (~a << 1)'], ['NegI8', 'i8', 'long', '-a'], ['NotI8', 'i8', 'long', '~a'],
  ['ConvI1', 'i4', 'int', '(sbyte)a'], ['ConvU1', 'i4', 'int', '(byte)a'], ['ConvI2', 'i4', 'int', '(short)a'], ['ConvU2', 'i4', 'int', '(ushort)a'],
  ['ConvOvfI1', 'i4', 'int', 'checked((sbyte)a)'], ['ConvOvfU1', 'i4', 'int', 'checked((byte)a)'], ['ConvOvfI2', 'i4', 'int', 'checked((short)a)'],
  ['ConvOvfU2', 'i4', 'int', 'checked((ushort)a)'], ['ConvOvfU4', 'i4', 'uint', 'checked((uint)a)'], ['ConvOvfI4Un', 'u4', 'int', 'checked((int)a)'],
  ['ConvI4I8', 'i8', 'int', '(int)a'], ['ConvOvfI4I8', 'i8', 'int', 'checked((int)a)'], ['ConvOvfU4I8', 'i8', 'uint', 'checked((uint)a)'],
  ['ConvI8I4', 'i4', 'long', '(long)a'], ['ConvU8U4', 'u4', 'ulong', '(ulong)a'], ['ConvOvfU8I8', 'i8', 'ulong', 'checked((ulong)a)'],
  ['ConvOvfI8U8', 'u8', 'long', 'checked((long)a)'], ['ConvOvfI1I8', 'i8', 'int', 'checked((sbyte)a)'],
  ['CmpI4', 'i4i4', 'int', 'a < b ? -1 : a > b ? 1 : 0'], ['CmpUnI4', 'u4u4', 'int', 'a < b ? -1 : a > b ? 1 : 0'],
  ['CmpI8', 'i8i8', 'int', 'a < b ? -1 : a > b ? 1 : 0'], ['CmpUnI8', 'u8u8', 'int', 'a <= b ? (a == b ? 0 : -1) : 1'],
  ['AddR8', 'r8r8', 'double', 'a + b'], ['SubR8', 'r8r8', 'double', 'a - b'], ['MulR8', 'r8r8', 'double', 'a * b'], ['DivR8', 'r8r8', 'double', 'a / b'],
  ['RemR8', 'r8r8', 'double', 'a % b'], ['MulAddR8', 'r8r8', 'double', 'a * b + a'], ['SqDiffR8', 'r8r8', 'double', '(a + b) * (a - b)'],
  ['CmpR8', 'r8r8', 'int', '(a == b ? 1 : 0) | (a < b ? 2 : 0) | (a > b ? 4 : 0) | (!(a <= b) ? 8 : 0) | (!(a >= b) ? 16 : 0) | (a != b ? 32 : 0)'],
  ['NegR8', 'r8', 'double', '-a'], ['ConvR4R8', 'r8', 'float', '(float)a'], ['ConvI4R8', 'r8', 'int', '(int)a'],
  ['ConvOvfI4R8', 'r8', 'int', 'checked((int)a)'], ['ConvU4R8', 'r8', 'uint', '(uint)a'], ['ConvOvfU4R8', 'r8', 'uint', 'checked((uint)a)'],
  ['ConvI8R8', 'r8', 'long', '(long)a'], ['ConvOvfI8R8', 'r8', 'long', 'checked((long)a)'], ['ConvU8R8', 'r8', 'ulong', '(ulong)a'],
  ['ConvOvfU8R8', 'r8', 'ulong', 'checked((ulong)a)'], ['ConvI2R8', 'r8', 'int', '(short)a'], ['ConvU1R8', 'r8', 'int', '(byte)a'],
  ['ConvR8I4', 'i4', 'double', '(double)a'], ['ConvR8I8', 'i8', 'double', '(double)a'], ['ConvRUnU4', 'u4', 'double', '(double)a'],
  ['ConvRUnU8', 'u8', 'double', '(double)a'], ['ConvR4I4', 'i4', 'float', '(float)a'], ['ConvR4I8', 'i8', 'float', '(float)a'],
  ['ConvR4U8', 'u8', 'float', '(float)a'],
  ['AddR4', 'r4r4', 'float', 'a + b'], ['MulR4', 'r4r4', 'float', 'a * b'], ['DivR4', 'r4r4', 'float', 'a / b'], ['RemR4', 'r4r4', 'float', 'a % b'],
  ['MulMulR4', 'r4r4', 'float', 'a * b * b'], ['MulAddR4', 'r4r4', 'float', 'a * b + a'], ['MixR4R8', 'r4r4', 'double', 'a * (double)b + a'],
  ['CmpR4', 'r4r4', 'int', '(a == b ? 1 : 0) | (a < b ? 2 : 0) | (!(a <= b) ? 8 : 0) | (!(a >= b) ? 16 : 0)'],
  ['ConvR8R4', 'r4', 'double', '(double)a'], ['ConvI4R4', 'r4', 'int', '(int)a'], ['ConvU8R4', 'r4', 'ulong', '(ulong)a'],
];
// kernels with statements (stores to float locals and fields, switch, value types, arrays, casts, clauses)
const KS = `
        static float s_f; static double s_d; static int s_i; static long s_l;
        public static float StoreR4(float a, float b) { float t = a * b; s_f = t + a; float u = s_f; return u * b; }
        public static double StoreR8(double a, double b) { double t = a * b; s_d = t + a; return s_d - b; }
        public static int StaticI4(int a, int b) { s_i = a; s_i += b; s_l = (long)s_i * a; return s_i ^ (int)(s_l >> 7); }
        public static int Sw(int a)
        {
            switch (a) { case 0: return 10; case 1: return 11; case 2: return 12; case 3: return 13; case 7: return 17; case 16: return 26; default: return -a; }
        }
        public static int SwSparse(int a)
        {
            switch (a) { case -7: return 1; case 32: return 2; case 0x8000: return 3; case int.MaxValue: return 4; case int.MinValue: return 5; default: return 0; }
        }
        public static int Unbox(object o) { return (int)o; }
        public static long UnboxL(object o) { return (long)o; }
        public static int Cast(object o) { string s = (string)o; return s.Length; }
        public static int IsInst(object o) { string s = o as string; if (s != null) return s.Length; JitKO k = o as JitKO; return k != null ? k.v : -1; }
        public static object Box(int a, int b) { if (b > 0) return a; if (b < 0) return (long)a; return null; }
        public static int LdelemI1(sbyte[] a, int i) { return a[i]; }
        public static int LdelemU2(ushort[] a, int i) { return a[i]; }
        public static long LdelemI8(long[] a, int i) { return a[i]; }
        public static double LdelemR8(double[] a, int i) { return a[i]; }
        public static float LdelemR4(float[] a, int i) { return a[i]; }
        public static object LdelemRef(object[] a, int i) { return a[i]; }
        public static int Stelem(int[] a, int i, int v) { a[i] = v; return a[i] + a.Length; }
        public static int StelemRef(object[] a, int i, object v) { a[i] = v; return a[i] == null ? 0 : 1; }
        public static int NewArr(int n) { int[] a = new int[n]; return a.Length; }
        public static int Ldlen(int[] a) { return a.Length; }
        public static int RefAdd(ref int x, int y) { x += y; return x; }
        public static int StructMove(JitPt p, int d) { JitPt q = p; q.X += d; JitPt r = q; r.Y = q.X * 2; return r.X + r.Y + p.X; }
        public static int StructArr(JitPt[] a, int i) { JitPt t = a[i]; a[i].X = t.Y; return a[i].X - t.X; }
        public static int TryCatch(int a)
        {
            int r = 0;
            try { if (a < 0) throw new ArgumentException("neg"); r = 100 / a; }
            catch (ArgumentException) { r = -1; }
            catch (DivideByZeroException) { r = -2; }
            finally { r += 1000; }
            return r;
        }
        public static int Nested(int a)
        {
            try { try { return checked(a * a); } finally { s_i = a; } } catch (OverflowException) { return s_i; }
        }
        public static string Str(int a) { string s = "ab"; if (a > 0) s = s + a; return s; }
`;
// superinstruction patterns (docs/130 §9.1): instance kernels of JitKO, compiled through "JitKO.*". Graphs come from MakeKO.
const KO = `
    public class JitKO
    {
        public JitKO next; public int v; public object o; public int[] arr; public object[] objs; public int size;
        public object S1AB() { return o; }
        public int S1AD() { return v; }
        public static object S1AF(JitKO a) { return a.next.o; }
        public object S1B3() { return next.next; }
        public bool S1B4(JitKO p) { JitKO x = p; if (x == next) return true; return x == this; }
        public int S1B2(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }
        public static int S1B8(int a, int b) { int x = a; int y = b; if (x < y) return -1; if (y < x) return 1; return 0; }
        public int S1BC() { JitKO p = this; int n = 0; while (p != null) { p = p.next; n++; } return n; }
        public int S1BE() { if (o != null) return 1; if (size < next.arr.Length) return 2; return 3; }
        public object S1BF(int h) { int n = size; int b = (int)((uint)(h & 0x7FFFFFFF) % (uint)n); object e = objs[b]; if (e == null) return null; return e; }
        public int S1B9() { JitKO2 t = this as JitKO2; if (t != null) return t.w; return -1; }
        public static bool S1BA(object a, object b) { return a == b; }
        public static bool S1BD(object a, object b) { if (a != null) return a == b; return b == null; }
        public static int S1B0(JitKO a) { JitKO p = a; if (p == null) return 0; return p.next == null ? 1 : 2; }
        public static int S1B7(JitKO a) { JitKO p; if ((p = a.next) == null) return 0; return p.v; }
        public static bool S1AC(bool b) { if (b) return false; return true; }
        public static bool S1B5(object o) { object p = o; return p != null; }
        public bool S1B6() { return next != null; }
        public static object S1BB(object[] a, int i, object x, object y) { object[] t = a; if (x == y) return t[i]; return null; }
        public static int S1B1(JitPt p, object o) { return p.Sum(o); }
        public int Walk() { int s = 0; JitKO p = this; for (int i = 0; i < 64 && p != null; i++) { s += p.v + (p.arr != null ? p.arr.Length : 0); p = p.next; } return s; }
    }
    public class JitKO2 : JitKO { public int w; }
    public struct JitPt { public int X, Y; public int Sum(object o) { return X + Y + (o == null ? 0 : 1); } }
`;

// ---- cases ----
type Case = { name: string; body: string };
function kernelCases(): Case[] {
  return K.map(([n, kind]) => {
    const [, arrs, args] = P[kind], a = arrs.split(',');
    const loops = a.length === 2 ? `for (int i = 0; i < ${a[0]}.Length; i++) for (int j = 0; j < ${a[1]}.Length; j++)` : `for (int i = 0; i < ${a[0]}.Length; i++)`;
    return { name: `JitK.${n} (${kind})`, body: `${loops} { try { h = M(h, JitK.${n}(${args})); } catch (Exception e) { h = X(h, e); } }` };
  });
}
const T = (call: string) => `try { h = M(h, ${call}); } catch (Exception e) { h = X(h, e); }`;
const STMT_CASES: Case[] = [
  { name: 'JitK.StoreR4 / StoreR8 (float stores)', body: `for (int i = 0; i < R4.Length; i++) for (int j = 0; j < R4.Length; j++) ${T('JitK.StoreR4(R4[i], R4[j])')}
            for (int i = 0; i < R8.Length; i++) for (int j = 0; j < R8.Length; j++) ${T('JitK.StoreR8(R8[i], R8[j])')}` },
  { name: 'JitK.StaticI4 (static fields)', body: `for (int i = 0; i < I4.Length; i++) for (int j = 0; j < I4.Length; j++) ${T('JitK.StaticI4(I4[i], I4[j])')}` },
  { name: 'JitK.Sw / SwSparse (switch)', body: `for (int i = 0; i < I4.Length; i++) { ${T('JitK.Sw(I4[i])')} ${T('JitK.SwSparse(I4[i])')} }` },
  { name: 'JitK.Unbox / UnboxL / Cast / IsInst / Box', body: `object[] os = new object[] { null, 5, 5L, "x", new JitKO(), new JitKO2(), (short)3, DayOfWeek.Monday };
            for (int i = 0; i < os.Length; i++) { ${T('JitK.Unbox(os[i])')} ${T('JitK.UnboxL(os[i])')} ${T('JitK.Cast(os[i])')} ${T('JitK.IsInst(os[i])')} }
            for (int i = 0; i < I4.Length; i++) for (int j = 0; j < 3; j++) { object b = JitK.Box(I4[i], j - 1); h = M(h, b == null ? 0 : b.GetHashCode()); }` },
  { name: 'JitK.LdelemI1 / LdelemU2 / LdelemI8 (bounds)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[] { -128, -1, 127 }; ushort[] us = new ushort[] { 0, 0x8000, 0xFFFF }; long[] ls = new long[] { long.MinValue, -1, long.MaxValue };
            double[] ds = new double[] { D(0x7FF0000000000001UL), -0.0, 1e308 }; float[] fs = new float[] { F(0x7F800001U), F(0x80000000U), F(0x00000001U) };
            object[] ob = new object[] { null, "s", 3 }; object[] st = new string[3]; int[] ia = new int[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.LdelemI1(sb, i)')} ${T('JitK.LdelemU2(us, i)')} ${T('JitK.LdelemI8(ls, i)')}
            }` },
  { name: 'JitK.LdelemR8 / LdelemR4 (bounds, sNaN, -0, denormal)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[] { -128, -1, 127 }; ushort[] us = new ushort[] { 0, 0x8000, 0xFFFF }; long[] ls = new long[] { long.MinValue, -1, long.MaxValue };
            double[] ds = new double[] { D(0x7FF0000000000001UL), -0.0, 1e308 }; float[] fs = new float[] { F(0x7F800001U), F(0x80000000U), F(0x00000001U) };
            object[] ob = new object[] { null, "s", 3 }; object[] st = new string[3]; int[] ia = new int[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.LdelemR8(ds, i)')} ${T('JitK.LdelemR4(fs, i)')}
            }` },
  { name: 'JitK.LdelemRef / Stelem (bounds)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[] { -128, -1, 127 }; ushort[] us = new ushort[] { 0, 0x8000, 0xFFFF }; long[] ls = new long[] { long.MinValue, -1, long.MaxValue };
            double[] ds = new double[] { D(0x7FF0000000000001UL), -0.0, 1e308 }; float[] fs = new float[] { F(0x7F800001U), F(0x80000000U), F(0x00000001U) };
            object[] ob = new object[] { null, "s", 3 }; object[] st = new string[3]; int[] ia = new int[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.LdelemRef(ob, i)')} ${T('JitK.Stelem(ia, i, i * 3)')}
            }` },
  { name: 'JitK.StelemRef (covariant string[]: store a string, then an int)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[] { -128, -1, 127 }; ushort[] us = new ushort[] { 0, 0x8000, 0xFFFF }; long[] ls = new long[] { long.MinValue, -1, long.MaxValue };
            double[] ds = new double[] { D(0x7FF0000000000001UL), -0.0, 1e308 }; float[] fs = new float[] { F(0x7F800001U), F(0x80000000U), F(0x00000001U) };
            object[] ob = new object[] { null, "s", 3 }; object[] st = new string[3]; int[] ia = new int[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.StelemRef(st, i, "v")')} ${T('JitK.StelemRef(st, i, (object)5)')}
            }` },
  { name: 'JitK.LdelemI1 on null', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[] { -128, -1, 127 }; ushort[] us = new ushort[] { 0, 0x8000, 0xFFFF }; long[] ls = new long[] { long.MinValue, -1, long.MaxValue };
            double[] ds = new double[] { D(0x7FF0000000000001UL), -0.0, 1e308 }; float[] fs = new float[] { F(0x7F800001U), F(0x80000000U), F(0x00000001U) };
            object[] ob = new object[] { null, "s", 3 }; object[] st = new string[3]; int[] ia = new int[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.LdelemI1(null, i)')}
            }` },
  { name: 'JitK.NewArr (0, positive, negative size)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[] { -128, -1, 127 }; ushort[] us = new ushort[] { 0, 0x8000, 0xFFFF }; long[] ls = new long[] { long.MinValue, -1, long.MaxValue };
            double[] ds = new double[] { D(0x7FF0000000000001UL), -0.0, 1e308 }; float[] fs = new float[] { F(0x7F800001U), F(0x80000000U), F(0x00000001U) };
            object[] ob = new object[] { null, "s", 3 }; object[] st = new string[3]; int[] ia = new int[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.NewArr(i < 100 ? i : 100)')}
            }` },
  { name: 'JitK.Ldlen (null, array)', body: `int[] ia = new int[3]; ${T('JitK.Ldlen(null)')} ${T('JitK.Ldlen(ia)')}` },
  { name: 'JitK.RefAdd / StructMove / StructArr (byrefs, value types)', body: `for (int i = 0; i < I4.Length; i++)
            {
                int x = I4[i]; ${T('JitK.RefAdd(ref x, 7)')} h = M(h, x);
                JitPt p; p.X = I4[i]; p.Y = -I4[i]; ${T('JitK.StructMove(p, 3)')}
                JitPt[] pa = new JitPt[2]; pa[1].Y = I4[i]; ${T('JitK.StructArr(pa, i & 3)')} ${T('JitK.StructArr(null, 0)')}
            }` },
  { name: 'JitK.Str (ldstr, concat)', body: `for (int i = 0; i < I4.Length; i++) { string s = JitK.Str(I4[i]); h = M(h, s.Length); h = M(h, s.GetHashCode()); }` },
  { name: 'JitKO superinstructions 0x1AB-0x1C0 (graphs with null links, short arrays, zero size)', body: `JitKO[] g = MakeKO();
            for (int k = 0; k < g.Length; k++)
            {
                JitKO a = g[k];
                ${T('a.S1AB()')} ${T('a.S1AD()')} ${T('JitKO.S1AF(a)')} ${T('a.S1B3()')} ${T('a.S1B4(a.next)')} ${T('a.S1B4(null)')}
                ${T('a.S1B2(k * 3)')} ${T('JitKO.S1B8(k, 3)')} ${T('JitKO.S1B8(3, k)')} ${T('a.S1BC()')} ${T('a.S1BE()')}
                ${T('a.S1BF(k * 7919)')} ${T('a.S1BF(int.MinValue)')} ${T('a.S1B9()')} ${T('JitKO.S1BA(a, a.next)')} ${T('JitKO.S1BA(a.o, a.o)')}
                ${T('JitKO.S1BD(a.o, null)')} ${T('JitKO.S1BD(null, a.next)')} ${T('JitKO.S1B0(a)')} ${T('JitKO.S1B0(a.next)')} ${T('JitKO.S1B7(a)')}
                ${T('JitKO.S1AC((k & 1) == 0)')} ${T('JitKO.S1B5(a.o)')} ${T('a.S1B6()')} ${T('JitKO.S1BB(a.objs, k - 1, a.o, a.o)')}
                ${T('JitKO.S1BB(a.objs, k, a, null)')} JitPt p; p.X = k; p.Y = 2; ${T('JitKO.S1B1(p, a.o)')} ${T('a.Walk()')}
            }
            ${T('JitKO.S1AF(null)')} ${T('JitKO.S1B7(null)')}` },
];
function bclCases(): Case[] {
  const dict = (ty: string, key: (i: string) => string, label: string): Case => ({
    name: `Dictionary<${label},int> (add, grow, overwrite, remove + re-add, TryGetValue, ContainsKey, enumerate)`,
    body: `int[] sizes = new int[] { 0, 1, 3, 7, 17, 100, 1000 };
            for (int s = 0; s < sizes.Length; s++)
            {
                Dictionary<${ty}, int> d = new Dictionary<${ty}, int>();
                for (int i = 0; i < sizes[s]; i++) d[${key('i')}] = i;
                for (int i = 0; i < sizes[s]; i += 3) d[${key('i')}] = -i;
                for (int i = 1; i < sizes[s]; i += 2) d.Remove(${key('i')});
                for (int i = 1; i < sizes[s]; i += 4) d.Add(${key('i')}, i * 5);
                for (int i = -2; i < sizes[s] + 2; i++) { int v; h = M(h, d.TryGetValue(${key('i')}, out v) ? v : 77); h = M(h, d.ContainsKey(${key('i')})); }
                foreach (KeyValuePair<${ty}, int> kv in d) { h = M(h, kv.Value); h = M(h, kv.Key.GetHashCode()); }
                h = M(h, d.Count);
                ${T(`AddTwice(d, ${key('1')})`)}
                ${T('ModifyWhileEnumerating(d)')}
            }`,
  });
  return [
    dict('int', i => `${i} * 7`, 'int'), dict('string', i => `"k" + ${i}`, 'string'), dict('ulong', i => `(ulong)${i} * 0x100000001UL`, 'ulong'),
    dict('ObjectGuid', i => `new ObjectGuid((ulong)${i} * 977UL)`, 'ObjectGuid'), dict('JitKey', i => `new JitKey(${i})`, 'colliding struct key'),
    dict('DayOfWeek', i => `(DayOfWeek)(${i} % 9)`, 'enum'),
    { name: 'Dictionary<string,int> null key', body: `Dictionary<string, int> d = new Dictionary<string, int>(); ${T('NullKey(d)')} int v; ${T('d.TryGetValue(null, out v)')}` },
    { name: 'List<int> / List<object> (growth, indexer bounds, enumerator, modification)', body: `for (int n = 0; n < 40; n++)
            {
                List<int> l = new List<int>(); List<object> lo = new List<object>(n & 3);
                for (int i = 0; i < n; i++) { l.Add(i * i); lo.Add(i % 3 == 0 ? null : (object)i); }
                h = M(h, l.Count); h = M(h, lo.Count);
                ${T('l[n]')} ${T('l[n - 1]')} ${T('l[-1]')} ${T('lo[n / 2]')}
                foreach (int x in l) h = M(h, x);
                ${T('ModifyList(l)')}
            }` },
    { name: 'Array.Resize / Array.IndexOf (null, negative size, missing, null elements)', body: `int[] sz = new int[] { -1, 0, 1, 2, 5, 64, 65 };
            for (int s = 0; s < sz.Length; s++)
            {
                int[] a = null; ${T('ResizeI(ref a, sz[s])')} int[] b = new int[] { 1, 2, 3 }; ${T('ResizeI(ref b, sz[s])')}
                if (b != null) { h = M(h, b.Length); for (int i = 0; i < b.Length; i++) h = M(h, b[i]); }
                string[] c = new string[] { "a", null, "c" }; ${T('ResizeS(ref c, sz[s])')}
                ${T('Array.IndexOf(b, sz[s])')} ${T('Array.IndexOf(c, null)')} ${T('Array.IndexOf(c, (object)"c")')} ${T('Array.IndexOf((int[])null, 1)')}
            }` },
    { name: 'Enum.Equals / Enum.GetHashCode (underlying types, other enum types, null)', body: `object[] es = new object[] { DayOfWeek.Monday, DayOfWeek.Sunday, (JitE8)1, (JitE8)255, (JitE64)(-1L), (JitE64)long.MaxValue, (JitEU64)ulong.MaxValue, 1, null };
            for (int i = 0; i < es.Length; i++)
            {
                if (es[i] != null) h = M(h, es[i].GetHashCode());
                for (int j = 0; j < es.Length; j++) if (es[i] != null) ${T('es[i].Equals(es[j])')}
            }` },
    { name: 'Type.GetTypeFromHandle / RuntimeTypeHandle', body: `Type[] ts = new Type[] { typeof(int), typeof(List<int>), typeof(Dictionary<string, JitKO>), typeof(JitPt[]), typeof(JitKO2) };
            for (int i = 0; i < ts.Length; i++) { h = M(h, ts[i].Name.GetHashCode()); h = M(h, ts[i] == typeof(int)); h = M(h, Type.GetTypeFromHandle(ts[i].TypeHandle) == ts[i]); }` },
    { name: 'UInt64 / Int32 GetHashCode + Equals, EqualityComparer default / IEquatable', body: `for (int i = 0; i < I8.Length; i++)
            {
                ulong u = (ulong)I8[i]; int n = (int)I8[i];
                h = M(h, u.GetHashCode()); h = M(h, n.GetHashCode()); h = M(h, u.Equals((object)u)); h = M(h, u.Equals((object)n)); h = M(h, n.Equals((object)(long)n));
                h = M(h, EqualityComparer<ulong>.Default.GetHashCode(u)); h = M(h, EqualityComparer<int>.Default.Equals(n, (int)u));
                h = M(h, EqualityComparer<JitKey>.Default.Equals(new JitKey(n), new JitKey(n + 4)));
                h = M(h, EqualityComparer<object>.Default.Equals(u, u)); h = M(h, EqualityComparer<JitPt>.Default.GetHashCode(new JitPt()));
            }` },
    { name: 'ObjectGuid == / != (0, max, near values)', body: `ulong[] g = new ulong[] { 0, 1, 977, ulong.MaxValue, ulong.MaxValue - 1, 0x100000000UL };
            for (int i = 0; i < g.Length; i++) for (int j = 0; j < g.Length; j++) { h = M(h, new ObjectGuid(g[i]) == new ObjectGuid(g[j])); h = M(h, new ObjectGuid(g[i]) != new ObjectGuid(g[j])); }` },
    { name: 'CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags (bit patterns)', body: `for (int b = 0; b < 32; b++) for (int k = 0; k < 4; k++)
            {
                uint f = (1u << b) | (uint)(k * 0x1111);
                h = M(h, (uint)Sims3.SimIFace.CAS.CASUtils.CASAGSAvailabilityFlagsFromCASAgeGenderFlags((Sims3.SimIFace.CAS.CASAgeGenderFlags)f));
            }` },
    { name: 'Single.IsNaN / Math.Max / Math.Min (NaN payloads, -0, inf)', body: `for (int i = 0; i < R4.Length; i++) h = M(h, float.IsNaN(R4[i]));
            for (int i = 0; i < R8.Length; i++) for (int j = 0; j < R8.Length; j++) { h = M(h, Math.Max(R8[i], R8[j])); h = M(h, Math.Min(R8[i], R8[j])); }
            for (int i = 0; i < R4.Length; i++) for (int j = 0; j < R4.Length; j++) { h = M(h, Math.Max(R4[i], R4[j])); h = M(h, Math.Min(R4[i], R4[j])); }
            for (int i = 0; i < I4.Length; i++) for (int j = 0; j < I4.Length; j++) { h = M(h, Math.Max(I4[i], I4[j])); h = M(h, Math.Min(I4[i], I4[j])); }
            for (int i = 0; i < I8.Length; i++) for (int j = 0; j < I8.Length; j++) { h = M(h, Math.Max(I8[i], I8[j])); h = M(h, Math.Min(I8[i], I8[j])); }` },
  ];
}
// methods with clauses last: P0 compiles them unverified, and a CPU fault in one stops the corpus
const CLAUSE_CASES: Case[] = [
  { name: 'JitK.TryCatch, no exception (try, finally)', body: `${T('JitK.TryCatch(5)')} ${T('JitK.TryCatch(int.MaxValue)')}` },
  { name: 'JitK.TryCatch, ArgumentException thrown and caught', body: `${T('JitK.TryCatch(-1)')} ${T('JitK.TryCatch(int.MinValue)')}` },
  { name: 'JitK.TryCatch, DivideByZeroException from div.i4 caught', body: `${T('JitK.TryCatch(0)')}` },
  { name: 'JitK.Nested, no overflow (return through finally)', body: `${T('JitK.Nested(3)')} ${T('JitK.Nested(-46340)')}` },
  { name: 'JitK.Nested, OverflowException through finally into catch', body: `${T('JitK.Nested(46341)')} ${T('JitK.Nested(int.MinValue)')}` },
];
export const cases = (): Case[] => [...kernelCases(), ...STMT_CASES, ...bclCases(), ...CLAUSE_CASES];

// ---- C# ----
export function generate(): string {
  const cs = cases(), kern = K.map(([n, kind, ret, ex]) => `        public static ${ret} ${n}(${P[kind][0]}) { return ${ex}; }`);
  return `// GENERATED by tools/jit-corpus.ts --write (round 6 B4, docs/207 §10). Do not edit: change the generator.
// Differential corpus for the monohost method compiler: JitCorpus.Run(c) returns one hash of case c's results and caught
// exceptions. monohost (MONOHOST_JITCORPUS=1) runs each case twice interpreted and once compiled. C# 2 (no lambdas).
using System;
using System.Collections.Generic;
using Sims3.SimIFace;

namespace MonoHostTest
{
    public static class JitK
    {
${kern.join('\n')}
${KS.replace(/^\n|\n$/g, '')}
    }
${KO.replace(/^\n|\n$/g, '')}
    public struct JitKey : IEquatable<JitKey>
    {
        public int K;
        public JitKey(int k) { K = k; }
        public bool Equals(JitKey o) { return o.K == K; }
        public override bool Equals(object o) { return o is JitKey && ((JitKey)o).K == K; }
        public override int GetHashCode() { return K & 3; }
    }
    public enum JitE8 : byte { A = 1 }
    public enum JitE64 : long { A = 1 }
    public enum JitEU64 : ulong { A = 1 }

    public static class JitCorpus
    {
        public const int Count = ${cs.length};
        static readonly int[] I4 = { ${I4.join(', ')} };
        static readonly long[] I8 = { ${I8.join(', ')} };
        static readonly int[] SH = { ${SH.join(', ')} };
        static readonly double[] R8 = { ${R8.join(', ')} };
        static readonly float[] R4 = { ${R4.join(', ')} };
        static double D(ulong bits) { return BitConverter.Int64BitsToDouble(unchecked((long)bits)); }
        static float F(uint bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
        static int M(int h, int v) { return unchecked(h * 31 + v); }
        static int M(int h, uint v) { return M(h, unchecked((int)v)); }
        static int M(int h, long v) { return M(M(h, unchecked((int)v)), (int)(v >> 32)); }
        static int M(int h, ulong v) { return M(h, unchecked((long)v)); }
        static int M(int h, bool v) { return M(h, v ? 1 : 2); }
        static int M(int h, double v) { return M(h, BitConverter.DoubleToInt64Bits(v)); }
        static int M(int h, float v) { return M(h, BitConverter.ToInt32(BitConverter.GetBytes(v), 0)); }
        static int M(int h, object v) { return M(h, v == null ? 3 : v.GetHashCode()); }
        // exception: type and the throwing frame (first stack-trace line: method and IL offset)
        static int X(int h, Exception e)
        {
            string s = e.StackTrace, f = "";
            if (s != null) { int nl = s.IndexOf('\\n'); f = (nl < 0 ? s : s.Substring(0, nl)).Trim(); }
            if (s_trail != null && s_trail.Length < 1100) s_trail.Append(e.GetType().Name).Append(' ').Append(f).Append(" | ");
            return M(M(M(h, 0x5EED), e.GetType().FullName.GetHashCode()), Frame(f).GetHashCode());
        }
        // "#0: 0x00008 div.i4     in Ns.Class:Method (int,int) (0CFB8D60 [0] [0] )" -> "#0: 0x00008 Ns.Class:Method (int,int)"
        // (the argument values, \`this\` first, hold object addresses)
        static string Frame(string f)
        {
            int sp = f.IndexOf(' ', 4), at = f.IndexOf(" in "), ps = at < 0 ? -1 : f.IndexOf(" (", at + 4), pe = ps < 0 ? -1 : f.IndexOf(')', ps);
            string ip = sp > 0 ? f.Substring(0, sp) : f;
            return at < 0 ? ip : ip + " " + (pe < 0 ? f.Substring(at + 4) : f.Substring(at + 4, pe + 1 - at - 4));
        }
        static System.Text.StringBuilder s_trail;
        public static string Trail(int c) { s_trail = new System.Text.StringBuilder(); Run(c); string t = s_trail.ToString(); s_trail = null; return t; }
        static JitKO[] MakeKO()
        {
            JitKO a = new JitKO(), b = new JitKO(), c = new JitKO2(), e = new JitKO();
            a.next = b; b.next = c; c.next = null; a.v = 1; b.v = 2; c.v = 3; ((JitKO2)c).w = 9;
            a.o = "o"; b.o = null; c.o = a; a.arr = new int[4]; b.arr = new int[0]; c.arr = null;
            a.objs = new object[] { null, "x", a }; b.objs = new object[0]; c.objs = null; a.size = 3; b.size = 0; c.size = 7; e.size = -1;
            return new JitKO[] { a, b, c, e };
        }
        static int AddTwice<T>(Dictionary<T, int> d, T k) { d[k] = 1; d.Add(k, 2); return d.Count; }
        static int ModifyWhileEnumerating<T>(Dictionary<T, int> d) { foreach (KeyValuePair<T, int> kv in d) d[kv.Key] = 0; if (d.Count > 0) foreach (T k in d.Keys) d.Remove(k); return d.Count; }
        static int NullKey(Dictionary<string, int> d) { d[null] = 1; return d.Count; }
        static int ModifyList(List<int> l) { foreach (int x in l) if (x > 3) l.Add(x); return l.Count; }
        static int ResizeI(ref int[] a, int n) { Array.Resize(ref a, n); return a.Length; }
        static int ResizeS(ref string[] a, int n) { Array.Resize(ref a, n); return a.Length; }

        public static int Run(int caseId)
        {
            int h = 17;
            switch (caseId)
            {
${cs.map((k, i) => `                case ${i}: // ${k.name}
                {
            ${k.body}
                    return h;
                }`).join('\n')}
            }
            return int.MinValue;
        }
    }
}
`.replace(/\r?\n/g, '\r\n');
}

// P0's default list from monohost-jit.cpp (kJitList) + the corpus kernel classes
export function jitList(src: string): string {
  const m = /kJitList\[\] = \{([\s\S]*?)\};/.exec(src);
  if (!m) throw new Error('kJitList not found in monohost-jit.cpp');
  return [...m[1].replace(/\/\/[^\n]*/g, '').matchAll(/"([^"]+)"/g)].map(x => x[1]).concat('JitK.*', 'JitKO.*').join(',');
}

// ---- report ----
export type Report = { cases: number; differ: number; unstable: number; lines: string[]; verify?: string; superops: string[]; missingSuper: string[] };
export function report(log: string, dump: string | undefined, names: string[]): Report {
  const r: Report = { cases: 0, differ: 0, unstable: 0, lines: [], superops: [], missingSuper: [] };
  for (const l of log.split(/\r?\n/)) {
    let m = /^jit corpus: (\d+) cases, (\d+) differ, (\d+) unstable;(.*)$/.exec(l);
    if (m) { r.cases = +m[1]; r.differ = +m[2]; r.unstable = +m[3]; r.verify = m[4].trim(); continue; }
    m = /^jit corpus (MISMATCH|UNSTABLE|FAULT) case (\d+): (.*)$/.exec(l);
    if (m) { r.lines.push(`${m[1]} ${m[2]} ${names[+m[2]] ?? '?'}: ${m[3]}`); continue; }
    m = /^jit corpus trail case \d+ (interpreted|compiled): (.*)$/.exec(l);
    if (m) r.lines.push(`    ${m[1]}: ${m[2].slice(0, 400)}`);
  }
  if (dump) {  // superops seen in the compiled corpus kernels (mint listing of each compiled JitK / JitKO method)
    const seen = new Set<string>();
    let inK = false;
    for (const l of dump.split(/\r?\n/)) {
      if (l.startsWith('== ')) { inK = /\.JitKO?\./.test(l); continue; }
      const m = inK && /^\s+[0-9A-F]{4} super_(1[A-C][0-9A-F])/.exec(l);
      if (m) seen.add(m[1]);
    }
    r.superops = [...seen].sort();
    for (let op = 0x1AB; op <= 0x1C0; op++) if (!seen.has(op.toString(16).toUpperCase())) r.missingSuper.push(op.toString(16).toUpperCase());
  }
  return r;
}

function selfCheck() {
  let k = 0;
  const ok = (c: boolean, what: string) => { k++; if (!c) { console.log(`FAIL ${what}`); process.exitCode = 1; } };
  const g = generate(), cs = cases();
  ok(g === generate(), 'generation is deterministic');
  ok(new Set(cs.map(c => c.name)).size === cs.length, 'case names are unique');
  ok(g.includes(`public const int Count = ${cs.length};`) && (g.match(/^ {16}case \d+: \/\//gm) ?? []).length === cs.length, 'one switch arm per case');
  ok(!/=>|\bvar\b/.test(g.replace(/"[^"\r\n]*"/g, '')), 'C# 2 only (no lambdas, no var)');
  let disk = '';
  try { disk = readFileSync(OUT, 'utf8'); } catch { /* missing = stale */ }
  ok(disk.replace(/\r\n/g, '\n') === g.replace(/\r\n/g, '\n'), 'native/monohost/JitCorpus.cs is up to date (node tools/jit-corpus.ts --write)');
  const src = 'static const char* const kJitList[] = {\n    // c\n    "A.B", "C`2.D",\n    "E.F",\n};';
  ok(jitList(src) === 'A.B,C`2.D,E.F,JitK.*,JitKO.*', 'kJitList parse');
  const log = 'jit corpus MISMATCH case 1: compiled 0000002A interpreted 0000002B\njit corpus trail case 1 compiled: DivideByZeroException at X | \njit corpus UNSTABLE case 2: interpreted 1 then 2\n'
    + 'jit corpus: 3 cases, 1 differ, 1 unstable; per-call verify during the corpus: ok 5, MISMATCH 0, abandoned (exception) 2\n';
  const dump = '== MonoHostTest.JitKO.S1AB rm 0 words 3\n  0000 super_1AB        0000\n== Other.M rm 0\n  0000 super_1AD\n';
  const r = report(log, dump, ['a', 'b', 'c']);
  ok(r.cases === 3 && r.differ === 1 && r.unstable === 1 && r.lines[0] === 'MISMATCH 1 b: compiled 0000002A interpreted 0000002B'
    && r.lines[1] === '    compiled: DivideByZeroException at X | ', 'log parse');
  ok(r.superops.join() === '1AB' && r.missingSuper.length === 21 && !r.missingSuper.includes('1AB'), 'superop coverage from the dump');
  console.log(`jit-corpus self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else if (process.argv.includes('--write')) { const g = generate(); writeFileSync(OUT, g); console.log(`wrote ${OUT}: ${cases().length} cases, ${K.length} arithmetic kernels`); }
  else if (process.argv.includes('--env')) console.log(jitList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8')));
  else if (arg('--report')) {
    const d = arg('--dump'), r = report(readFileSync(arg('--report')!, 'utf8'), d ? readFileSync(d, 'utf8') : undefined, cases().map(c => c.name));
    if (!r.cases) { console.log('jit-corpus: no "jit corpus:" summary line (MONOHOST_JITCORPUS=1 set? MonoHostTest.dll built with JitCorpus.cs?)'); process.exitCode = 1; }
    else console.log(`jit-corpus: ${r.cases} cases, ${r.differ} differ, ${r.unstable} unstable; ${r.verify}`);
    for (const l of r.lines) console.log(`  ${l}`);
    if (d) console.log(`superops in the compiled kernels: ${r.superops.length} of 22${r.missingSuper.length ? ` (missing ${r.missingSuper.join(' ')})` : ''}`);
    const review = r.lines.length + (r.cases ? 0 : 1);
    console.log(`needs review: ${review ? `${review} item(s) above` : 'none'}`);
  } else console.log('usage: --write | --env | --report <monohost.exe.log> [--dump <jit-dump.txt>] | --self-check');
}
