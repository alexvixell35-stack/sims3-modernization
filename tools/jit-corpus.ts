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
//   [MONOHOST_JITCORPUS_FROM=<first case> MONOHOST_JITCORPUS_TO=<last case>] [MONOHOST_JITCORPUS_TRAIL=1: every case's interpreted exception trail]
//   node tools/jit-corpus.ts --index <name prefix>   first case index with that name prefix (the accessor fixtures: --index "Acc ")
//   monohost.exe <repo>\game-copy\monohost\TS3W.exe <repo>\game-copy\assemblies selftest <repo>\game-copy\monohost\hostdata.txt
// Yields are out of scope: P0 compiles only never-yielding methods, and monohost runs no task scheduler (P1 adds them).
// Round 8 B6 (docs/207 §9.1): the ACCESSOR cases call the Sims3Gameplay* accessor fixtures of native/monohost/JitAccessors.cs
// (hand-written; build.cmd compiles it against the game's Sims3GameplaySystems.dll); --env lists those 17 accessors too.
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
  ['CmpR8V', 'r8r8', 'int', '((a == b) == (a > b) ? 1 : 0) | ((a < b) == (!(a <= b)) ? 2 : 0) | ((!(a >= b)) == (a < b) ? 4 : 0)'],
  ['CmpR4', 'r4r4', 'int', '(a == b ? 1 : 0) | (a < b ? 2 : 0) | (!(a <= b) ? 8 : 0) | (!(a >= b) ? 16 : 0)'],
  ['ConvR8R4', 'r4', 'double', '(double)a'], ['ConvI4R4', 'r4', 'int', '(int)a'], ['ConvU8R4', 'r4', 'ulong', '(ulong)a'],
];
// kernels with statements (stores to float locals and fields, switch, value types, arrays, casts, clauses)
const KS = `
        static float s_f; static double s_d; static int s_i; static long s_l;
        public static float StoreR4(float a, float b) { float t = a * b; s_f = t + a; float u = s_f; return u * b; }
        public static double StoreR8(double a, double b) { double t = a * b; s_d = t + a; return s_d - b; }
        public static int StaticI4(int a, int b) { s_i = a; s_i += b; s_l = (long)s_i * a; return s_i ^ (int)(s_l >> 7); }
        // round 10 C2: static fields of every width (ldsfld / stsfld typed by the field), references, the array stores, stobj,
        // the string intrinsics, field addresses and float fields
        static object s_o; static byte s_b; static short s_s; static char s_c; static bool s_bool;
        public static int StaticMix(int a, int b)
        {
            s_b = (byte)a; s_s = (short)b; s_c = (char)(a ^ b); s_bool = a < b; s_o = a == 0 ? null : (object)a;
            s_l = ((long)a << 32) | (uint)b; s_d = a * 0.5; s_f = b * 0.25f;
            return s_b + s_s + s_c + (s_bool ? 1 : 0) + (s_o == null ? 0 : ((int)s_o & 0xFF)) + (int)(s_l >> 31) + (int)s_d + (int)s_f;
        }
        public static int StaticRef(object a) { s_o = a; object t = s_o; return t == null ? 0 : t is int ? (int)t : 7; }
        public static int StelemI1(sbyte[] a, int i, int v) { a[i] = (sbyte)v; return a[i]; }
        public static int StelemI2(short[] a, int i, int v) { a[i] = (short)v; return a[i]; }
        public static long StelemI8(long[] a, int i, long v) { a[i] = v; return a[i]; }
        public static float StelemR4(float[] a, int i, double v) { a[i] = (float)v; return a[i]; }
        public static double StelemR8(double[] a, int i, double v) { a[i] = v; return a[i]; }
        public static int StelemRefK(JitKO[] a, int i, JitKO v) { a[i] = v; return a[i] == null ? 0 : a[i].v + 1; }
        // docs/537 §16: a local names a large graph the entry snapshot does not hold, and the heap gets a new array
        public static object[] FloodA, FloodB;
        public static int KeepFlood(JitKO b, int i) { object[] g = i % 2 == 0 ? FloodA : FloodB; object[] n = new object[3]; n[0] = b.next; b.objs = n; return g.Length + n.Length; }
        static int StobjT<T>(ref T r, T v) { r = v; return 1; }
        public static int StobjUse(int a, object o)
        {
            int x = 0; StobjT(ref x, a); object y = null; StobjT(ref y, o); long l = 0; StobjT(ref l, (long)a << 20); double d = 0; StobjT(ref d, a * 0.5);
            return x + (y == null ? 0 : 1) + (int)(l >> 20) + (int)d;
        }
        public static int StrLenChr(string s, int i) { return s.Length * 1000 + s[i]; }
        public static int FldAddr(JitKO o, int d) { return RefAdd(ref o.v, d); }
        public static double FldR(JitKF o, float a, double b) { o.f = a; o.d = b; return o.f + o.d; }
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
        public static int CatchOnly(int a) { int r; try { r = 100 / a; } catch (DivideByZeroException) { r = -2; } return r; }
        public static int FinallyOnly(int a) { int r = a; try { r += 1; } finally { r *= 2; } return r; }
        public static string Str(int a) { string s = "ab"; if (a > 0) s = s + a; return s; }
`;
// superinstruction patterns (docs/130 §9.1): instance kernels of JitKO, compiled through "JitKO.*". Graphs come from MakeKO.
// EA's peephole fuses a run only when the whole run between two boundaries (method start, branch target, call / ret /
// newobj ...) is the sequence (docs/139 §4.1), so kernels start a run at a ternary join or after a call.
const KO = `
    public class JitKO
    {
        public JitKO next; public int v; public object o; public int[] arr; public object[] objs; public int size;
        public object S1AB() { return o; }
        public int S1AD() { return v; }
        public static object S1AF(JitKO a, JitKO b, bool c) { return (c ? a : b).next.o; }
        public object S1B3() { return next.next; }
        public bool S1B4(JitKO p, JitKO q, bool c) { JitKO x; if (c) x = p; else x = q; return Same(x, next); }
        static bool Same(object a, object b) { return a == b; }
        public int S1B2(int n) { int s = 0; for (int i = 0; i < n; i++) s += i; return s; }
        public static int S1B8(int a, int b) { int x = a; int y = b; if (x < y) return -1; if (y < x) return 1; return 0; }
        public int S1BC() { JitKO p = this; int n = 0; while (p != null) { p = p.next; n++; } return n; }
        public int S1BE() { if (o != null) return 1; if (size < next.arr.Length) return 2; return 3; }
        public object S1BF(int h) { int n = size; int b = (int)((uint)(h & 0x7FFFFFFF) % (uint)n); object e = objs[b]; if (e == null) return null; return e; }
        public object S1B9() { JitKO2 t = this as JitKO2; if (t != null) return t; return null; }
        public static bool S1BA(object a, object b) { return a == b; }
        public static bool S1BD(object a, object b) { if (a != null) return a == b; return b == null; }
        public static object S1B0(JitKO p, bool a, bool b, bool c) { JitKO x = p; if (a ? b : c) return x.next; return null; }
        public static int S1B7(JitKO a) { JitKO p; if ((p = a.next) == null) return 0; return p.v; }
        public static int S1AC(bool a, bool b, bool c) { if (!(a ? b : c)) return 0; return 1; }
        public static int S1B5(object a, object b, bool c) { object x; if (c) x = a; else x = b; if (x == null) return 0; return 1; }
        public object S1B6() { if (next == null) return null; return next.o; }
        public bool S1C0(object p) { JitKO k = p as JitKO; return k != null && next == k.next && o == k.o; }
        public static object S1BB(object[] a, int i, object x, object y) { object[] t = a; if (x == y) return t[i]; return null; }
        public static int S1B1(JitPt p, object o) { return p.Sum(o); }
        public int Walk() { int s = 0; JitKO p = this; for (int i = 0; i < 64 && p != null; i++) { s += p.v + (p.arr != null ? p.arr.Length : 0); p = p.next; } return s; }
    }
    public class JitKO2 : JitKO { public int w; }
    public class JitKF { public float f; public double d; }
    public struct JitPt { public int X, Y; public int Sum(object o) { return X + Y + (o == null ? 0 : 1); } }
`;
// round 10 C4 (docs/207 §14): exceptions and stack walks through compiled frames. Thrower throws InvalidOperationException
// at 0 and DivideByZeroException at 1; Mid / MidCatch / EnvMid / DeepT are lazy middle frames (each has a branch so mint
// does not inline it). Names hashes the method name of every stack-trace line ("... in Ns.Class:Method (params) (addr)")
// down to the last JitKX frame.
const KX = `
    public class JitKX
    {
        public static int s_log;
        static int Thrower(int a) { if (a == 0) throw new InvalidOperationException("x"); return 100 / (a - 1); }
        static int Mid(int a) { try { return Thrower(a) + 1; } finally { s_log = s_log * 10 + 1; } }
        static int MidCatch(int a) { try { return Thrower(a); } catch (DivideByZeroException) { return 7; } }
        static int Names(string s)
        {
            int h = 17;
            if (s == null) return h;
            foreach (string line in s.Split('\\n'))
            {
                int at = line.IndexOf(" in "), ps = at < 0 ? -1 : line.IndexOf(" (", at + 4);
                if (at < 0) continue;
                string name = ps < 0 ? line.Substring(at + 4) : line.Substring(at + 4, ps - at - 4);
                if (name.IndexOf("JitKX") < 0) break;  // the frames above ours differ by caller (the corpus driver, the worker job)
                h = h * 31 + name.GetHashCode();
            }
            return h;
        }
        public static int FinallyMid(int a) { s_log = 0; try { return Mid(a) + 2; } catch (InvalidOperationException) { return -1000 - s_log; } catch (DivideByZeroException) { return -2000 - s_log; } }
        public static int CatchMid(int a) { return MidCatch(a) * 3 + a; }
        public static int Rethrow(int a) { s_log = 0; try { try { return Thrower(a); } catch (InvalidOperationException) { s_log++; throw; } } catch (InvalidOperationException e) { return -3 - s_log - e.Message.Length; } }
        public static int ThrowInFinally(int a) { try { try { return Thrower(a); } finally { if (a == 0) throw new ArgumentException("f"); } } catch (ArgumentException) { return -4; } }
        public static int TraceNames(int a) { s_log = 0; try { return Mid(a); } catch (Exception e) { return Names(e.StackTrace); } }
        static int EnvMid(int a)
        {
            if (a > 1000) return a;
            int h = 17; System.Diagnostics.StackFrame[] fs = new System.Diagnostics.StackTrace().GetFrames();
            if (fs != null) foreach (System.Diagnostics.StackFrame f in fs) { if (f.GetMethod() == null || f.GetMethod().DeclaringType != typeof(JitKX)) break; h = h * 31 + f.GetMethod().Name.GetHashCode(); }
            return h + a;
        }
        public static int EnvTrace(int a) { return EnvMid(a) + 1; }
        public static int Escape(int a) { return Thrower(a) + 1; }
        static int DeepT(int n) { if (n <= 0) return Thrower(0); return DeepT(n - 1) + 1; }
        public static int DeepCatch(int n) { try { return DeepT(n); } catch (InvalidOperationException e) { return n + Names(e.StackTrace); } }
    }
`;
// round 10 C1 (docs/207 §15): the value-type moves. JitTPt is 12 bytes: Take runs stinarg.vt, ldarg.vt, stloc.vt and
// starg.vt; Locals ldloc.vt / stloc.vt and a struct call (con.call with a pointer this); Result the vtresult after a
// struct-returning call and a call on the temp; Dup the dup.vt of a chained assignment; Big a 20-byte struct (two
// cursor steps of 8 each side of the copy). JitKTF (C1 leftovers): the value-type field loads ldfld.vt / ldsfld.vt
// (Fields, Statics; S has a .cctor, so the first call takes the class-init exit) and the null-receiver exit (Null).
// JitKCV: con.callvirt on a reference type (a constrained call in a generic method instantiated with a class): Eq through
// IEquatable (guarded, one class), Hash through Object.GetHashCode with the class flipping every call (the guard refills),
// Null a null reference behind the pointer (the site exits, EA throws).
const KT = `
    public struct JitTPt { public int X, Y, Z; public JitTPt(int x, int y, int z) { X = x; Y = y; Z = z; } public int Sum() { return X + Y + Z; } }
    public struct JitTBig { public int A, B, C, D, E; public int Sum() { return A + B * 3 + C + D + E; } }
    public class JitKT
    {
        static JitTPt Mk(int a) { return new JitTPt(a, a * 2, a - 1); }
        static int Take(JitTPt p, int k) { JitTPt q = p; q.X += k; p.Y = q.X; return p.Sum() * 3 + q.Z; }
        public static int Locals(int n) { int s = 0; for (int i = 0; i < n; i++) { JitTPt p = Mk(i); JitTPt q = p; q.Y = -q.Y; s += Take(q, i) + p.Sum(); } return s; }
        public static int Result(int n) { int s = 0; for (int i = 0; i < n; i++) s += Mk(i).Sum() + Mk(i + 1).X; return s; }
        public static int Dup(int n) { int s = 0; for (int i = 0; i < n; i++) { JitTPt a, b; a = b = Mk(i); b.Z = 9; s += a.X + a.Z + b.Z; } return s; }
        static JitTBig MkBig(int a) { JitTBig b; b.A = a; b.B = a + 1; b.C = a + 2; b.D = a + 3; b.E = a + 4; return b; }
        static int TakeBig(JitTBig b, int k) { JitTBig c = b; c.E = k; b.A = c.E; return b.Sum() + c.Sum(); }
        public static int Big(int n) { int s = 0; for (int i = 0; i < n; i++) { JitTBig b = MkBig(i); s += TakeBig(b, i) + MkBig(i).Sum(); } return s; }
    }
    public class JitKTF
    {
        public JitTPt F; public JitTBig G; public static JitTPt S = new JitTPt(7, 8, 9); public static JitTBig SB;
        static int Sum3(JitTBig b) { return b.Sum(); }
        public static int Fields(JitKTF o, int n) { int s = 0; for (int i = 0; i < n; i++) { JitTPt p = o.F; p.X += i; o.F = p; s += p.Sum() + Sum3(o.G); } return s; }
        public static int Statics(int n) { int s = 0; for (int i = 0; i < n; i++) { JitTPt p = S; s += p.Y + Sum3(SB) + i; } return s; }
        public static int Null(JitKTF o) { try { JitTPt p = o.F; return p.X; } catch (NullReferenceException) { return -7; } }
    }
    public class JitRKey : IEquatable<JitRKey>
    {
        public int V; public JitRKey(int v) { V = v; }
        public bool Equals(JitRKey o) { if (o == null) return false; return o.V == V; }
        public override int GetHashCode() { if (V < 0) return 1; return V * 31 + 1; }
    }
    public class JitRKey2 : JitRKey { public JitRKey2(int v) : base(v) { } public override int GetHashCode() { if (V < 0) return 2; return V * 7 - 2; } }
    public class JitKCV
    {
        static int EqR<T>(T a, T b) where T : IEquatable<T> { if (a.Equals(b)) return 1; return 0; }
        static int HashR<T>(T a) { int h = a.GetHashCode(); if (h < 0) return -h; return h; }
        public static int Eq(int n) { int s = 0; JitRKey k = new JitRKey(2); for (int i = 0; i < n; i++) s += EqR(new JitRKey(i & 3), k); return s; }
        public static int Hash(int n) { int s = 0; for (int i = 0; i < n; i++) s += HashR((i & 1) == 0 ? new JitRKey(i) : new JitRKey2(i)); return s; }
        public static int Null(int n) { try { JitRKey k = null; return HashR(k) + n; } catch (NullReferenceException) { return -9; } }
    }
`;
// round 10 C5 (docs/207 §16.3): initobj (a local, a heap field through ldflda, an array element, every size path: inline dwords
// and the memset of a size not a multiple of 4; generic default(T) with T = int, long, a class, a struct), isinst (null, classes
// at each depth, interfaces hit and missed, arrays and remoting classes left to EA's handler, a generic T, inside EA's
// 0x1B9 / 0x1C0 superops through Equals), shr.un.i8 past the width, and the two direct internal calls (typeof, Enum).
const KC5 = `
    public interface JitIA { int A(); }
    public interface JitIB { int B(); }
    public class JitC5A : JitIA { public int V; public virtual int A() { return 1; } public override bool Equals(object o) { JitC5A x = o as JitC5A; if (x == null) return false; return x.V == V; } public override int GetHashCode() { return V; } }
    public class JitC5B : JitC5A, JitIB { public int B() { return 2; } }
    public class JitC5C : JitC5B { public override int A() { return 3; } }
    public sealed class JitC5S { }
    public class JitC5M : MarshalByRefObject { }
    public enum JitEnI { A, B = 7 }
    public enum JitEnB : byte { A, B = 200 }
    public enum JitEnL : long { A, B = 0x100000001L }
    public struct JitOdd { public int A, B, C, D, E; public byte F; }
    public struct JitQ { public int A, B, C, D; }
    public class JitKI
    {
        public JitTPt P; public JitOdd O; public object R; public JitQ Q;
        static T Def<T>() { T t = default(T); return t; }
        static bool IsG<T>(object o) { return o is T; }
        static object[] Objs() { return new object[] { null, "s", 1, 1L, 2.5, new JitC5A(), new JitC5B(), new JitC5C(), new JitC5S(), new JitC5M(), new int[2], new string[1], new object[1], new JitRKey[1], JitEnI.B, JitEnB.B, new List<int>(), new JitTPt(1, 2, 3), new object() }; }
        public static int Init(int n)
        {
            int s = 0; JitKI o = new JitKI(); JitOdd[] a = new JitOdd[3]; JitQ[] b = new JitQ[2];
            for (int i = 0; i < n; i++)
            {
                JitTPt p = new JitTPt(i, 2, 3); s += p.Sum(); p = new JitTPt(); s += p.Sum();
                o.P = new JitTPt(i, i, i); s += o.P.Sum(); o.P = new JitTPt(); s += o.P.Sum();
                a[i & 1].A = i; a[i & 1].F = 9; s += a[i & 1].F; a[i & 1] = new JitOdd(); s += a[i & 1].A + a[i & 1].F + a[2].F;
                o.O.E = i; o.O.F = 3; o.O = new JitOdd(); s += o.O.E + o.O.F;
                b[1].D = i; b[1] = new JitQ(); s += b[1].D; o.Q.A = i; o.Q = new JitQ(); s += o.Q.A;
                JitOdd l; l.A = i; l.B = 2; l.C = 3; l.D = 4; l.E = 5; l.F = 6; s += l.A; l = new JitOdd(); s += l.B + l.F;
            }
            return s;
        }
        public static long Gen(int n)
        {
            long s = 0;
            for (int i = 0; i < n; i++)
            {
                s += Def<int>() + Def<long>() + (long)Def<double>() + Def<JitTPt>().Sum() + Def<JitOdd>().F + (long)Def<byte>() + Def<JitQ>().B;
                if (Def<string>() != null) s += 9;
                if (Def<JitC5A>() != null) s += 11;
                if (Def<long?>().HasValue) s += 13;
            }
            return s;
        }
        // docs/537 §10, S5: float arithmetic, ldind and stind r4 and r8 through refs, ldstr, ldtoken, castclass
        static double RD(ref double a, ref float b, double c) { a = a * c + b; b = (float)(a / 3.0); return -a; }
        static string Str(int i) { return (i & 1) == 0 ? "even" : "odd"; }
        public static string CastS(object o) { return (string)o; }
        public static JitIA CastI(object o) { return (JitIA)o; }
        public static long Wide(int n)
        {
            double d = 1.5; float f = 0.25f; long s = 0;
            for (int i = 0; i < n; i++)
            {
                s += (long)RD(ref d, ref f, i + 0.5) + Str(i).Length + (int)(d / 7.0) + (short)(int)f + (long)(uint)(d - 1.0) + typeof(JitKI).Name.Length + (long)(float)i;
                if (d > 1e6 || d < -1e6) d = 1.5;
            }
            return s;
        }
        public static int NullField(JitKI o) { try { o.P = new JitTPt(); return 1; } catch (NullReferenceException) { return -3; } }
        public static int Is(int k)
        {
            int s = 0; object[] v = Objs();
            for (int i = 0; i < v.Length; i++)
            {
                object x = v[i]; int b = 0;
                if ((x as JitC5A) != null) b |= 1; if ((x as JitC5B) != null) b |= 2; if ((x as JitC5C) != null) b |= 4;
                if (x is JitIA) b |= 8; if (x is JitIB) b |= 16; if (x is JitC5S) b |= 32; if (x is string) b |= 64; if (x is int) b |= 128;
                if (x is JitEnI) b |= 256; if (x is Enum) b |= 512; if (x is ValueType) b |= 1024; if (x is object) b |= 2048;
                if (x is System.Collections.IEnumerable) b |= 4096; if (x is IList<int>) b |= 8192; if (x is IComparable) b |= 16384;
                if (x is MarshalByRefObject) b |= 32768; if (x is int[]) b |= 65536; if (x is object[]) b |= 131072; if (x is Array) b |= 262144;
                if (x is JitRKey[]) b |= 524288; if (x is IEquatable<int>) b |= 1048576; if (x is List<int>) b |= 2097152;
                s = s * 31 + b + k;
            }
            return s;
        }
        public static int IsGen(int k)
        {
            int s = 0; object[] v = Objs();
            for (int i = 0; i < v.Length; i++)
            {
                object x = v[i]; int b = 0;
                if (IsG<JitC5B>(x)) b |= 1; if (IsG<JitIB>(x)) b |= 2; if (IsG<int>(x)) b |= 4; if (IsG<List<int>>(x)) b |= 8; if (IsG<string[]>(x)) b |= 16; if (IsG<JitEnB>(x)) b |= 32;
                s = s * 31 + b + k;
            }
            return s;
        }
        public static int Eq(int n) { int s = 0; JitC5A a = new JitC5A(), b = new JitC5B(); object[] v = Objs(); for (int i = 0; i < n; i++) { a.V = i & 1; if (a.Equals(b)) s++; if (a.Equals(v[i % v.Length])) s += 3; } return s; }
        // docs/537 §8: a lazy body with a cold exit: the list changes under the enumerator, VerifyState's throw block runs from a lazy MoveNext
        public static int Cold(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                List<int> l = new List<int>(); l.Add(i); l.Add(2); Dictionary<int, int> d = new Dictionary<int, int>(); d[i] = 1; d[i + 1] = 2;
                try { foreach (int x in l) { s += x; if (i % 3 == 0) l.Add(1); } } catch (InvalidOperationException) { s += 1000; }
                try { foreach (KeyValuePair<int, int> p in d) { s += p.Value; if (i % 3 == 1) d[i + 5] = 3; } } catch (InvalidOperationException) { s += 2000; }
            }
            return s;
        }
        public static long Shr(int n)
        {
            long s = 0; ulong x = 0x8000000180000001UL;
            for (int i = 0; i < n; i++) { int k = i * 7 - 40; s = s * 3 + (long)(x >> k) + (long)((ulong)(long)(i - 7) >> 32) + (long)x.GetHashCode(); x = x * 5 + 3; }
            return s;
        }
        public static int Types(int n)
        {
            int s = 0; object[] v = Objs();
            for (int i = 0; i < n; i++)
            {
                if (typeof(int) == typeof(long)) s++; if (typeof(List<int>).Name.Length > 1) s += 2; if (typeof(JitTPt).IsValueType) s += 3; if (typeof(int[]).IsArray) s += 5; if (typeof(JitIA).IsInterface) s += 7;
                object o = v[(i % (v.Length - 1)) + 1]; if (o.GetType() == typeof(string)) s += 11;
                if (typeof(int).TypeHandle.Value != IntPtr.Zero) s += 13;
            }
            return s;
        }
        public static int Enums(int n)
        {
            int s = 0; object a = JitEnI.B, b = JitEnB.B, c = JitEnL.B, d = JitEnI.A;
            for (int i = 0; i < n; i++) { s = s * 3 + a.GetHashCode() + b.GetHashCode() + c.GetHashCode() + d.GetHashCode(); if (a.Equals(d)) s++; if (a.Equals(JitEnI.B)) s += 2; if (a.Equals(b)) s += 3; if (a.Equals(null)) s += 5; if (c.Equals(JitEnL.B)) s += 7; }
            return s;
        }
        public static float Vec(int n)
        {
            float s = 0f;
            for (int i = 0; i < n; i++) { Sims3.SimIFace.Vector3 p = new Sims3.SimIFace.Vector3(i, 2f, 0.5f); Sims3.SimIFace.Vector3 q = new Sims3.SimIFace.Vector3(p); Sims3.SimIFace.Vector2 w = new Sims3.SimIFace.Vector2(1f, i); Sims3.SimIFace.Vector3 r = new Sims3.SimIFace.Vector3(w); s += q.x + r.y; }
            return s;
        }
    }
`;
// B3 lazy frames (docs/207 §12): compiled callers calling compiled static leaves (every position native, ret / ret.void).
// Each leaf has a branch so mint's transform does not inline it; Arr / Set / Deref fault inside the leaf (frame written at
// the exit, then EA's handler throws).
const KL = `
    public class JitIN
    {
        public int v; public byte b; public short h; public float f; public long w; public double d; public JitIN next; public int[] a; public string t;
        public int V() { return v; }
        public int VB() { return b; }
        public int VH() { return h; }
        public float F() { return f; }
        public long W() { return w; }
        public double D() { return d; }
        public JitIN Next() { return next; }
        public int NextV() { return next.v; }
        public int Next2V() { return next.next.v; }
        public int ALen() { return a.Length; }
        public int Add3(int x) { return v + x + 3; }
        public bool Eq(int x) { return v == x; }
        public int Not() { return ~v; }
        public void Nop() { }
    }
    public struct JitINS
    {
        public int X; public byte B; public JitIN R;
        public int GetX() { return X; }
        public int GetB() { return B; }
        public int RV() { return R.v; }
        public int XP(int k) { return X + k; }
    }
    public class JitIND : JitIN { public int o; public new int V() { return o; } }
    public static class JitKIN
    {
        public static long Run(int n)
        {
            JitIN a = new JitIN(), b = new JitIN(), c = new JitIN(), nul = null;
            a.v = 3; a.b = 200; a.h = -5; a.f = 1.5f; a.w = 0x1122334455667788L; a.d = -2.5; a.next = b; a.a = new int[7]; b.v = 11; b.next = c; c.v = 13;
            JitIND dd = new JitIND(); dd.v = 1; dd.o = 2; dd.next = a;
            JitINS st = new JitINS(); st.X = 9; st.B = 250; st.R = b;
            JitINS sn = new JitINS(); sn.X = -1;
            long r = 0;
            for (int i = 0; i < n; i++)
            {
                r += Hot((i & 1) == 0 ? a : dd, a, b, dd, ref st, i);
                try { r += N1(nul); } catch (NullReferenceException) { r += 1; }
                try { N2(nul); r += 100; } catch (NullReferenceException) { r += 2; }
                try { r += N3(b); } catch (NullReferenceException) { r += 3; }
                try { r += N4(c); } catch (NullReferenceException) { r += 5; }
                try { r += N5(b); } catch (NullReferenceException) { r += 7; }
                try { r += N6(ref sn); } catch (NullReferenceException) { r += 11; }
            }
            return r;
        }
        static long Hot(JitIN p, JitIN a, JitIN b, JitIND dd, ref JitINS st, int i)
        {
            long r = p.V() + p.VB() + p.VH() + (long)p.F() + p.W() + (long)p.D() + p.NextV() + p.Add3(i) + (p.Eq(i) ? 1 : 0) + p.Not() + dd.V();
            p.Nop();
            r += a.Next2V() + a.ALen() + (p.Next() == b ? 1 : 0);
            return r + st.GetX() + st.GetB() + st.RV() + st.XP(i);
        }
        static int N1(JitIN p) { return p.V() + 1; }
        static void N2(JitIN p) { p.Nop(); }
        static int N3(JitIN p) { return p.Next2V() + 1; }
        static int N4(JitIN p) { return p.NextV() + 1; }
        static int N5(JitIN p) { return p.ALen() + 1; }
        static int N6(ref JitINS p) { return p.RV() + 1; }
    }
    public class JitKL
    {
        public int v; public long w; public JitKL next;
        static int Clamp(int x, int lo, int hi) { if (x < lo) return lo; if (x > hi) return hi; return x; }
        static bool Odd(int x) { if (x < 0) x = -x; return (x & 1) != 0; }
        static long Add8(long a, int b) { if (b > 2) return a + b; return a - b; }
        static int Locals(int a) { int t = a ^ 5; int u = t + 1; if (u > 5) u = 5; return t + u; }
        static int Arr(int[] a, int i) { if (i < -1) return -1; return a[i]; }
        static void Set(JitKL p, int x) { if (x > 0) p.v = x; else p.v = -x; }
        public int Get(int k) { if (k > 0) return v + k; return v - k; }
        public long GetW(int k) { if (k == 0) return w; return w + k; }
        public int Deref(JitKL p) { if (p.v > 0) return p.next.v; return v; }
        public static int CallStatic(int n) { int s = 0; for (int i = -n; i < n; i++) s += Clamp(i, -3, 3) + (Odd(i) ? 1 : 0) + Locals(i); return s; }
        public static long CallLong(int n) { long s = 0x7FFFFFF0L; for (int i = 0; i < n; i++) s = Add8(s, i); return s; }
        public static int CallInst(JitKL p, int n) { int s = 0; for (int i = -n; i < n; i++) s += p.Get(i); return s; }
        public static long CallInstLong(JitKL p, int n) { long s = 0; for (int i = -n; i < n; i++) s += p.GetW(i); return s; }
        public static int CallVoid(JitKL p, int n) { for (int i = -n; i < n; i++) Set(p, i); return p.v; }
        public static int CallArr(int[] a, int i) { return Arr(a, i) + 1; }
        public static int CallDeref(JitKL p, JitKL q) { return p.Deref(q) + 1; }
        public static int CatchInCaller(JitKL p, JitKL q) { try { return p.Deref(q); } catch (NullReferenceException) { return -5; } }
        public static int Deep(int d, int x) { if (d == 0) return Clamp(x, 0, 9); return Deep(d - 1, x) + 1; }
        public static int CallStruct(int n) { JitKLPt p; p.X = 3; int s = 0; for (int i = -n; i < n; i++) s += p.Dot(i); return s; }
        // v2 (docs/207 §12.7): lazy bodies that make lazy calls (chains), exceptions below a chain, con.call
        static int Inner(int x) { if (x > 3) return x - 3; return 3 - x; }
        static int Mid(int x) { int t = Inner(x); if (t > 1000) return t; return t + Clamp(x, -2, 2); }
        static int Mid2(JitKL p, int x) { int t = p.Get(x); if (t > 1000) return t; return t + Inner(x); }
        static int Outer(int x) { int t = Mid(x); if (t > 1000) return t; return t * 2; }
        static int ArrIn(int[] a, int i) { if (i < -1) return -1; return a[i]; }
        static int ArrMid(int[] a, int i) { int t = ArrIn(a, i); if (t > 1000) return t; return t + 1; }
        public static int CallChain(int n) { int s = 0; for (int i = -n; i < n; i++) s += Mid(i) + Outer(i); return s; }
        public static int CallChainInst(JitKL p, int n) { int s = 0; for (int i = -n; i < n; i++) s += Mid2(p, i); return s; }
        public static int CallChainArr(int[] a, int i) { return ArrMid(a, i) + 1; }
        public static int CatchChain(int[] a, int i) { try { return ArrMid(a, i); } catch (IndexOutOfRangeException) { return -9; } }
        public static int DeepChain(int d, int x) { if (d == 0) return Mid(x); return DeepChain(d - 1, x) + 1; }
        static int EqT<T>(T a, T b) where T : IEquatable<T> { if (a.Equals(b)) return 1; return 0; }
        public static int CallCon(int n) { int s = 0; for (int i = 0; i < n; i++) s += EqT(new JitKey(i & 3), new JitKey(2)); return s; }
    }
    public struct JitKLPt { public int X; public int Dot(int k) { if (k > 0) return X * k; return X - k; } }
    // v2: class-guarded callvirt (a virtual method through EA's inline cache); JitKV3.F divides by zero on even k
    public class JitKV
    {
        public virtual int F(int k) { if (k > 0) return k + 1; return -k; }
        public virtual int G(JitKV o, int k) { if (o == null) return -1; return o.F(k) + 1; }
        public static int CallVirt(JitKV o, int n) { int s = 0; for (int i = -n; i < n; i++) s += o.F(i); return s; }
        public static int CallVirtPoly(JitKV[] a, int n) { int s = 0; for (int i = 0; i < n; i++) s += a[i & 1].F(i); return s; }
        public static int CallVirtChain(JitKV o, JitKV p, int n) { int s = 0; for (int i = -n; i < n; i++) s += o.G(p, i); return s; }
    }
    public class JitKV2 : JitKV { public int m = 2; public override int F(int k) { if (k > 1) return k * m; return m - k; } }
    public class JitKV3 : JitKV { public override int F(int k) { if (k > 1000) return k; return k / (k & 1); } }
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
  { name: 'JitK.StaticMix / StaticRef (static field widths, references)', body: `for (int i = 0; i < I4.Length; i++) for (int j = 0; j < I4.Length; j++) ${T('JitK.StaticMix(I4[i], I4[j])')}
            object[] os2 = new object[] { null, 5, "s", new JitKO() };
            for (int k = 0; k < os2.Length; k++) ${T('JitK.StaticRef(os2[k])')}` },
  { name: 'JitK.StelemI1 / I2 / I8 / R4 / R8 (bounds, widths, rounding)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            sbyte[] sb = new sbyte[3]; short[] ss = new short[3]; long[] ls = new long[3]; float[] fs = new float[3]; double[] ds = new double[3];
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k], v = I4[k % I4.Length];
                ${T('JitK.StelemI1(sb, i, v)')} ${T('JitK.StelemI2(ss, i, v)')} ${T('JitK.StelemI8(ls, i, (long)v << 33)')}
            }
            for (int r = 0; r < R8.Length; r++) { ${T('JitK.StelemR4(fs, 1, R8[r])')} ${T('JitK.StelemR8(ds, 2, R8[r])')} }
            ${T('JitK.StelemR4(null, 0, 1.0)')} ${T('JitK.StelemR8(ds, 3, 1.0)')}` },
  { name: 'JitK.StelemRefK (exact class, a derived value, a covariant mismatch)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            JitKO[] ka = new JitKO[3]; JitKO[] kb = new JitKO2[3]; JitKO k1 = new JitKO(); JitKO k2 = new JitKO2(); k1.v = 3; k2.v = 5;
            for (int k = 0; k < ix.Length; k++)
            {
                int i = ix[k];
                ${T('JitK.StelemRefK(ka, i, null)')} ${T('JitK.StelemRefK(ka, i, k1)')} ${T('JitK.StelemRefK(ka, i, k2)')} ${T('JitK.StelemRefK(kb, i, k2)')} ${T('JitK.StelemRefK(kb, i, k1)')} ${T('JitK.StelemRefK(null, i, k1)')}
            }` },
  { name: 'JitK.StobjUse / StrLenChr / FldAddr / FldR (stobj, string intrinsics, field address, float fields)', body: `int[] ix = new int[] { -1, 0, 1, 2, 3, 4, int.MaxValue, int.MinValue };
            object[] os3 = new object[] { null, "x" }; string[] strs = new string[] { null, "", "abc", "\\u00e9\\ud83d" }; JitKF kf = new JitKF();
            for (int i = 0; i < I4.Length; i++) ${T('JitK.StobjUse(I4[i], os3[i & 1])')}
            for (int s = 0; s < strs.Length; s++) for (int k = 0; k < ix.Length; k++) ${T('JitK.StrLenChr(strs[s], ix[k])')}
            ${T('JitK.FldAddr(null, 1)')} ${T('JitK.FldAddr(new JitKO(), 5)')} ${T('JitK.FldR(null, 1f, 2.0)')}
            for (int i = 0; i < R4.Length; i++) for (int j = 0; j < R8.Length; j++) ${T('JitK.FldR(kf, R4[i], R8[j])')}` },
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
  // superop kernels, one case per kernel over the MakeKO graphs (null links, short arrays, zero size): [kernel, calls, calls after the loop, setup]
  ...([
    ['S1AB', ['a.S1AB()']], ['S1AD', ['a.S1AD()']], ['S1AF', ['JitKO.S1AF(a, a.next, c1)'], ['JitKO.S1AF(null, null, true)']],
    ['S1B3', ['a.S1B3()']], ['S1B4', ['a.S1B4(a.next, a, c1)', 'a.S1B4(null, a.next, c2)']], ['S1B2', ['a.S1B2(k * 3)']],
    ['S1B8', ['JitKO.S1B8(k, 3)', 'JitKO.S1B8(3, k)']], ['S1BC', ['a.S1BC()']], ['S1BE', ['a.S1BE()']],
    ['S1BF', ['a.S1BF(k * 7919)', 'a.S1BF(int.MinValue)']], ['S1B9', ['a.S1B9()']], ['S1BA', ['JitKO.S1BA(a, a.next)', 'JitKO.S1BA(a.o, a.o)']],
    ['S1BD', ['JitKO.S1BD(a.o, null)', 'JitKO.S1BD(null, a.next)']],
    ['S1B0', ['JitKO.S1B0(a, c1, c2, c3)', 'JitKO.S1B0(a.next, c3, c1, c2)'], ['JitKO.S1B0(null, true, true, false)']],
    ['S1B7', ['JitKO.S1B7(a)'], ['JitKO.S1B7(null)']], ['S1AC', ['JitKO.S1AC(c1, c2, c3)']], ['S1B5', ['JitKO.S1B5(a.o, a.next, c1)']],
    ['S1B6', ['a.S1B6()']], ['S1BB', ['JitKO.S1BB(a.objs, k - 1, a.o, a.o)', 'JitKO.S1BB(a.objs, k, a, null)']],
    ['S1C0', ['a.S1C0(a.next)', 'a.S1C0(a)', 'a.S1C0(a.o)']], ['S1B1', ['JitKO.S1B1(p, a.o)'], [], 'JitPt p; p.X = k; p.Y = 2;'], ['Walk', ['a.Walk()']],
  ] as [string, string[], string[]?, string?][]).map(([n, calls, after = [], pre = '']) => ({ name: `JitKO.${n} (superop kernel)`, body: `JitKO[] g = MakeKO();
            for (int k = 0; k < g.Length; k++)
            {
                JitKO a = g[k];
                bool c1 = (k & 1) == 0, c2 = (k & 2) == 0, c3 = (k & 4) == 0;
                ${pre}${calls.map(T).join(' ')}
            }
            ${after.map(T).join(' ')}` })),
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
    // docs/537 §22 BoundCall: the comparer's guarded s.GetHashCode() / s.Equals(t) sites, on EA's native String bodies with MONOHOST_JITBIND=1
    { name: 'String comparer: EqualityComparer<string>.Default GetHashCode/Equals, string.GetHashCode/Equals(string) (null, empty, non-ASCII, odd/even lengths, same reference)', body: `string[] ss = new string[] { null, "", "a", "ab", "abc", "k1", "k" + 1, "\\u00e9t\\u00e9", "\\u65e5\\u672c\\u8a9e", "\\ud83d\\ude00", new string('x', 257), "A", "a\\0b" };
            EqualityComparer<string> c = EqualityComparer<string>.Default;
            for (int i = 0; i < ss.Length; i++)
            {
                h = M(h, c.GetHashCode(ss[i])); ${T('ss[i].GetHashCode()')} ${T('ss[i].Length')}
                for (int j = 0; j < ss.Length; j++) { h = M(h, c.Equals(ss[i], ss[j])); h = M(h, ss[i] == ss[j]); if (ss[i] != null) h = M(h, ss[i].Equals(ss[j])); }
                if (ss[i] != null) { h = M(h, ss[i].Equals(ss[i])); h = M(h, ss[i].Equals((object)ss[i])); h = M(h, ss[i].Equals((object)i)); }
            }
            Dictionary<string, int> d = new Dictionary<string, int>(c);
            for (int i = 1; i < ss.Length; i++) d[ss[i]] = i;
            for (int i = 0; i < 200; i++) { int v; h = M(h, d.TryGetValue("k" + (i % 7), out v) ? v : -1); h = M(h, d.ContainsKey(ss[1 + i % (ss.Length - 1)])); }` },
    { name: 'List<int> / List<object> (growth, indexer bounds, enumerator, modification)', body: `for (int n = 0; n < 40; n++)
            {
                List<int> l = new List<int>(); List<object> lo = new List<object>(n & 3);
                for (int i = 0; i < n; i++) { l.Add(i * i); lo.Add(i % 3 == 0 ? null : (object)i); }
                h = M(h, l.Count); h = M(h, lo.Count);
                ${T('l[n]')} ${T('l[n - 1]')} ${T('l[-1]')} ${T('lo[n / 2]')}
                foreach (int x in l) h = M(h, x);
                ${T('ModifyList(l)')}
            }` },
    { name: 'List growth: size, version, capacity, elements (int, ulong, ObjectGuid, string, object)', body: `h = ListGrowth(h, new int[] { 1, -2, 3 });
            h = ListGrowth(h, new ulong[] { 1UL, 0x100000001UL, ulong.MaxValue });
            h = ListGrowth(h, new ObjectGuid[] { new ObjectGuid(5UL), new ObjectGuid(0x700000005UL), ObjectGuid.InvalidObjectGuid });
            h = ListGrowth(h, new string[] { "a", null, "bc" });
            h = ListGrowth(h, new object[] { null, "s", 5 });` },
    { name: 'List growth into dead cells', body: `for (int r = 0; r < 40; r++) h = ListGrowDead(h, r);` },
    { name: 'Kept set flood (old graph in a local, new array in the heap)', body: `if (JitK.FloodA == null) { JitK.FloodA = new object[3000]; JitK.FloodB = new object[3000]; for (int i = 0; i < 3000; i++) { JitK.FloodA[i] = new object[2]; JitK.FloodB[i] = new object[2]; } }
            JitKO k = new JitKO(); k.next = k;
            for (int i = 0; i < 64; i++) { h = M(h, JitK.KeepFlood(k, i)); h = M(h, k.objs.Length); h = M(h, k.objs[0] == k ? 1 : 0); }` },
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
  { name: 'JitK.CatchOnly, no exception (leave out of a try)', body: `${T('JitK.CatchOnly(5)')}` },
  { name: 'JitK.CatchOnly, DivideByZeroException caught', body: `${T('JitK.CatchOnly(0)')}` },
  { name: 'JitK.FinallyOnly (leave through a finally, endfinally)', body: `${T('JitK.FinallyOnly(5)')}` },
  { name: 'JitK.TryCatch, no exception (try, finally)', body: `${T('JitK.TryCatch(5)')} ${T('JitK.TryCatch(int.MaxValue)')}` },
  { name: 'JitK.TryCatch, ArgumentException thrown and caught', body: `${T('JitK.TryCatch(-1)')} ${T('JitK.TryCatch(int.MinValue)')}` },
  { name: 'JitK.TryCatch, DivideByZeroException from div.i4 caught', body: `${T('JitK.TryCatch(0)')}` },
  { name: 'JitK.Nested, no overflow (return through finally)', body: `${T('JitK.Nested(3)')} ${T('JitK.Nested(-46340)')}` },
  { name: 'JitK.Nested, OverflowException through finally into catch', body: `${T('JitK.Nested(46341)')} ${T('JitK.Nested(int.MinValue)')}` },
];
// lazy-frame cases last: a CPU fault in a lazy body would stop the rest of the corpus
const LAZY_CASES: Case[] = [
  { name: 'JitKL.CallStatic (lazy calls: static leaves, init locals)', body: T('JitKL.CallStatic(40)') },
  { name: 'JitKL.CallLong (lazy call, 8-byte return)', body: T('JitKL.CallLong(40)') },
  { name: 'JitKL.CallInst (lazy callvirt, non-virtual instance leaf; null receiver)', body: `JitKL p = new JitKL(); p.v = 7; ${T('JitKL.CallInst(p, 40)')} ${T('JitKL.CallInst(null, 3)')}` },
  { name: 'JitKL.CallInstLong (lazy callvirt, 8-byte return)', body: `JitKL p = new JitKL(); p.w = 0x123456789L; ${T('JitKL.CallInstLong(p, 40)')}` },
  { name: 'JitKL.CallVoid (void leaf with stfld; null: exception in the callee)', body: `JitKL p = new JitKL(); ${T('JitKL.CallVoid(p, 40)')} ${T('JitKL.CallVoid(null, 3)')}` },
  { name: 'JitKL.CallArr (bounds and null exceptions in a lazy callee)', body: `int[] a = new int[] { 4, 5, 6 }; ${T('JitKL.CallArr(a, 1)')} ${T('JitKL.CallArr(a, 3)')} ${T('JitKL.CallArr(a, -1)')} ${T('JitKL.CallArr(null, 0)')}` },
  { name: 'JitKL.CallDeref (NullReferenceException in a lazy callee)', body: `JitKL p = new JitKL(), q = new JitKL(); q.v = 1; ${T('JitKL.CallDeref(p, p)')} ${T('JitKL.CallDeref(p, q)')} q.next = p; ${T('JitKL.CallDeref(p, q)')}` },
  { name: 'JitKL.CatchInCaller (lazy callee throws, the compiled caller catches)', body: `JitKL p = new JitKL(), q = new JitKL(); q.v = 1; ${T('JitKL.CatchInCaller(p, q)')} q.next = q; ${T('JitKL.CatchInCaller(p, q)')}` },
  { name: 'JitKL.Deep (lazy call at depth 30 / 70 / 130)', body: `${T('JitKL.Deep(30, 4)')} ${T('JitKL.Deep(70, 12)')} ${T('JitKL.Deep(130, -2)')}` },
  { name: 'JitKL.CallStruct (lazy call, value-type this)', body: T('JitKL.CallStruct(40)') },
  // v2 (docs/207 §12.7): chains, exceptions below a chain, class-guarded callvirt, con.call
  { name: 'JitKL.CallChain (lazy chain: a lazy body with two lazy calls, and a chain of three)', body: T('JitKL.CallChain(40)') },
  { name: 'JitKL.CallChainInst (chain through a non-virtual callvirt; null receiver: NullReferenceException in the middle frame)', body: `JitKL p = new JitKL(); p.v = 5; ${T('JitKL.CallChainInst(p, 40)')} ${T('JitKL.CallChainInst(null, 3)')}` },
  { name: 'JitKL.CallChainArr (IndexOutOfRangeException two lazy frames down; CatchChain catches it in the compiled caller)', body: `int[] a = new int[] { 4, 5, 6 }; ${T('JitKL.CallChainArr(a, 1)')} ${T('JitKL.CallChainArr(a, 5)')} ${T('JitKL.CatchChain(a, 7)')} ${T('JitKL.CatchChain(a, 0)')}` },
  { name: 'JitKL.DeepChain (lazy chain under recursion at depth 30 / 70)', body: `${T('JitKL.DeepChain(30, 4)')} ${T('JitKL.DeepChain(70, -9)')}` },
  { name: 'JitKV.CallVirt (class-guarded callvirt: base class, derived class, null receiver)', body: `${T('JitKV.CallVirt(new JitKV(), 40)')} ${T('JitKV.CallVirt(new JitKV2(), 40)')} ${T('JitKV.CallVirt(null, 3)')}` },
  { name: 'JitKV.CallVirtPoly (two classes at one site: every call misses the cache and takes the normal exit)', body: `JitKV[] a = new JitKV[] { new JitKV(), new JitKV2() }; ${T('JitKV.CallVirtPoly(a, 40)')}` },
  { name: 'JitKV.CallVirtThrow (DivideByZeroException in a class-guarded lazy callee)', body: T('JitKV.CallVirt(new JitKV3(), 6)') },
  { name: 'JitKV.CallVirtChain (a guarded lazy callee makes a guarded lazy call; null inner receiver)', body: `${T('JitKV.CallVirtChain(new JitKV2(), new JitKV(), 40)')} ${T('JitKV.CallVirtChain(new JitKV(), null, 3)')}` },
  { name: 'JitKL.CallCon (con.call: a constrained call on a value type in a generic method)', body: T('JitKL.CallCon(40)') },
  { name: 'JitKT.Locals (ldloc.vt / stloc.vt / ldarg.vt / starg.vt / stinarg.vt on a 12-byte struct)', body: T('JitKT.Locals(40)') },
  { name: 'JitKT.Result (vtresult after a struct-returning call, a call on the temp)', body: T('JitKT.Result(40)') },
  { name: 'JitKT.Dup / Big (dup.vt of a chained assignment; a 20-byte struct through the same moves)', body: `${T('JitKT.Dup(40)')} ${T('JitKT.Big(40)')}` },
  { name: 'JitKTF.Fields / Statics / Null (ldfld.vt and ldsfld.vt into the vt stack; stfld.vt exits; a null receiver exits to the interpreter, which throws)', body: `JitKTF o = new JitKTF(); o.F = new JitTPt(1, 2, 3); o.G.E = 5; ${T('JitKTF.Fields(o, 40)')} ${T('JitKTF.Statics(40)')} ${T('JitKTF.Null(o)')} ${T('JitKTF.Null(null)')}` },
  // round 10 C4: exceptions and stack walks through compiled frames (docs/207 §14)
  { name: 'JitKX.FinallyMid (a finally in a lazy middle frame runs during the unwind; the compiled caller catches)', body: `${T('JitKX.FinallyMid(0)')} ${T('JitKX.FinallyMid(1)')} ${T('JitKX.FinallyMid(3)')}` },
  { name: 'JitKX.CatchMid (a catch in a lazy middle frame, then a return into the compiled caller; the other exception passes through)', body: `${T('JitKX.CatchMid(0)')} ${T('JitKX.CatchMid(1)')} ${T('JitKX.CatchMid(5)')}` },
  { name: 'JitKX.Rethrow / ThrowInFinally (rethrow out of a catch; a throw inside a finally replaces the exception)', body: `${T('JitKX.Rethrow(0)')} ${T('JitKX.Rethrow(2)')} ${T('JitKX.ThrowInFinally(0)')} ${T('JitKX.ThrowInFinally(1)')} ${T('JitKX.ThrowInFinally(4)')}` },
  { name: 'JitKX.TraceNames / DeepCatch (the method name of every stack-trace frame through lazy frames, depth 1 and 25)', body: `${T('JitKX.TraceNames(0)')} ${T('JitKX.TraceNames(1)')} ${T('JitKX.DeepCatch(25)')}` },
  { name: 'JitKX.EnvTrace (a StackTrace walk from a lazy callee: the method name of every frame)', body: T('JitKX.EnvTrace(3)') },
  { name: 'JitKX.Escape (an exception from a lazy callee reaches the driver through the compiled caller)', body: `${T('JitKX.Escape(0)')} ${T('JitKX.Escape(1)')} ${T('JitKX.Escape(2)')}` },
  // round 10 C1 leftovers: con.callvirt on a reference type
  { name: 'JitKCV.Eq / Hash / Null (con.callvirt on a class: this = *ptr, then the guarded callvirt; a class flip refills the guard; a null behind the pointer exits, EA throws)', body: `${T('JitKCV.Eq(40)')} ${T('JitKCV.Hash(40)')} ${T('JitKCV.Null(1)')}` },
  // round 10 C5: the §16.3 templates
  { name: 'JitKI.Init / Gen / NullField (initobj: locals, ldflda and ldelema targets, inline and memset sizes, default(T); a null receiver exits)', body: `${T('JitKI.Init(40)')} ${T('JitKI.Gen(40)')} ${T('JitKI.NullField(new JitKI())')} ${T('JitKI.NullField(null)')}` },
  { name: 'JitKI.Is / IsGen / Eq (isinst: null, class depths, interfaces, arrays, remoting, generic T, the isinst superops of Equals)', body: `${T('JitKI.Is(1)')} ${T('JitKI.Is(2)')} ${T('JitKI.IsGen(1)')} ${T('JitKI.Eq(60)')}` },
  { name: 'JitKI.Shr (shr.un.i8 at shifts past the width, negative shifts, UInt64.GetHashCode)', body: T('JitKI.Shr(40)') },
  { name: 'JitKI.Types / Enums (direct internal_from_handle and Enum.get_value, boxed enums of three widths)', body: `${T('JitKI.Types(40)')} ${T('JitKI.Enums(40)')}` },
  { name: 'JitKIN.Run (docs/537 §9: tiny getters inlined, a null at each base, a value-type this, a hiding method, r4 and r8 forms refused)', body: T('JitKIN.Run(40)') },
  { name: 'JitKI.Wide (docs/537 §10: float arithmetic, ldind and stind through refs, ldstr, ldtoken)', body: T('JitKI.Wide(64)') },
  { name: 'JitKI.CastS, CastI (castclass: null, match, mismatch throws)', body: `${T('JitKI.CastS(null)')} ${T('JitKI.CastS("a")')} ${T('JitKI.CastS(1)')} ${T('JitKI.CastI(null)')} ${T('JitKI.CastI("a")')}` },
  { name: 'JitKI.Cold (docs/537 §8: VerifyState\'s cold throw block through a lazy MoveNext, the frame written at the exit)', body: T('JitKI.Cold(24)') },
  { name: 'JitKI.Vec (Vector3 constructors from three floats, a Vector3, a Vector2)', body: T('JitKI.Vec(40)') },
];
// ---- docs/537 §20 A1 (CallExit, MONOHOST_JITCALLEXIT=1): one block, kept together for the hand merge with B3 ----
// Pure Array icalls (GetLength / GetLowerBound / Rank inline, bad dimension and null this exit to EA, which throws),
// boxed value-type receivers through a class-guarded callvirt (box + 4 for an override, the box itself for an inherited
// ValueType method, interface calls that mutate the box, polymorphic sites), callees whose hot exits keep their lazy bodies
// (newobj in the callee, exits at chain levels 3 to 5 now that B_LVL is right, an exception after the frames were written).
const KA = `
    public interface IJitKAF { int F(int k); int Bump(); }
    public struct JitKAS : IJitKAF
    {
        public int X;
        public int F(int k) { if (k > 0) return X + k; return X - k; }
        public int Bump() { X = X + 1; return X; }
        public override int GetHashCode() { if (X > 0) return X * 3; return 7; }
    }
    public struct JitKAN : IJitKAF
    {
        public int X; public long W;
        public int F(int k) { if (k > 2) return X * k; return k - X; }
        public int Bump() { X = X + 2; return X; }
    }
    public class JitKAC : IJitKAF
    {
        public int X;
        public int F(int k) { if (k > 0) return X ^ k; return X; }
        public int Bump() { X = X + 3; return X; }
    }
    public static class JitKA
    {
        static int Len(Array a, int d) { if (d < -100) return 0; return a.GetLength(d); }
        static int Low(Array a, int d) { if (d < -100) return 0; return a.GetLowerBound(d); }
        static int Rank(Array a, int k) { if (k > 100000) return 0; return a.Rank; }
        // the arrays come from the case body: verify mode pairs a compiled pass's fresh objects with the interpreter's only for plain allocations
        public static int Pure(Array a1, Array a2, Array a3, Array lb, int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                int d = i % 3;
                s += Len(a1, 0) + Len(a2, d & 1) + Len(a3, d) + Low(lb, d & 1) + Len(lb, d & 1) + Low(a2, d & 1) + Rank(a1, i) + Rank(a3, i) + Rank(lb, i);
            }
            return s;
        }
        public static int PureBad(Array a, int d) { return Len(a, d) * 10 + Low(a, d); }
        public static int Boxed(int n)
        {
            JitKAS s = new JitKAS(); s.X = 4; JitKAN t = new JitKAN(); t.X = 5; t.W = 9;
            object os = s, ot = t; IJitKAF fs = s;
            int r = 0;
            for (int i = -n; i < n; i++) r += os.GetHashCode() + ot.GetHashCode() + fs.F(i) + ((i & 7) == 0 ? fs.Bump() : 0);
            return r + ((JitKAS)fs).X * 1000 + ((JitKAS)os).X;
        }
        public static int BoxedPoly(int n)
        {
            IJitKAF[] a = new IJitKAF[3]; JitKAS s = new JitKAS(); s.X = 1; JitKAN t = new JitKAN(); t.X = 2; a[0] = s; a[1] = t; a[2] = new JitKAC();
            int r = 0;
            for (int i = 0; i < n; i++) { IJitKAF f = a[i % 3]; r += f.F(i) + f.Bump(); }
            for (int i = 0; i < n; i++) { IJitKAF f = a[(i / 8) % 3]; r += f.F(i - 20); }
            return r + ((JitKAS)a[0]).X * 1000 + ((JitKAN)a[1]).X * 100 + ((JitKAC)a[2]).X;
        }
        public static int BoxedNull(IJitKAF f, int k) { return f.F(k) + 1; }
        static JitKAC Mk(int k) { if (k > 100000) return null; JitKAC c = new JitKAC(); c.X = k; return c; }
        static int MkX(int k) { JitKAC c = Mk(k); if (c == null) return -1; return c.X + 1; }
        static int L5(int k) { if (k > 100000) return 0; return 100 / (k & 3) + Mk(k).X; }
        static int L4(int k) { int t = L5(k); if (t > 100000) return t; return t + 1; }
        static int L3(int k) { int t = L4(k); if (t > 100000) return t; return t + 2; }
        static int L2(int k) { int t = L3(k); if (t > 100000) return t; return t + 3; }
        static int L1(int k) { int t = L2(k); if (t > 100000) return t; return t + 4; }
        static int Thr(int k) { JitKAC c = Mk(k); return c.X / (k & 1); }
        public static int Callee(int n) { int s = 0; for (int i = 0; i < n; i++) s += Mk(i).X + MkX(i); return s; }
        public static int Chain5(int n) { int s = 0; for (int i = 1; i < n; i += 4) s += L1(i); return s; }
        public static int Chain5Throw(int k) { return L1(k) + 1; }
        public static int CatchChain5(int k) { try { return L1(k); } catch (DivideByZeroException) { return -7; } }
        public static int ThrowAfterMat(int k) { return Thr(k) + 1; }
        public static int Always(int n) { int s = 0; for (int i = 0; i < n; i++) s += MkX(i & 15); return s; }
    }
`;
const EXIT_CASES: Case[] = [
  { name: 'JitKA.Pure (pure Array icalls: szarray, rank 2 and 3, non-zero lower bounds, Rank)', body: `int[] a1 = new int[5]; int[,] a2 = new int[3, 4]; int[,,] a3 = new int[2, 3, 4]; Array lb = Array.CreateInstance(typeof(int), new int[] { 3, 2 }, new int[] { -2, 5 });
            ${T('JitKA.Pure(a1, a2, a3, lb, 40)')}` },
  { name: 'JitKA.PureBad (GetLength / GetLowerBound: dim past the rank, negative dim, null this; EA throws)', body: `int[] a1 = new int[2]; int[,] a2 = new int[2, 3];
            ${T('JitKA.PureBad(a1, 0)')} ${T('JitKA.PureBad(a1, 1)')} ${T('JitKA.PureBad(a1, -1)')} ${T('JitKA.PureBad(a2, 1)')} ${T('JitKA.PureBad(a2, 2)')} ${T('JitKA.PureBad(null, 0)')}` },
  { name: 'JitKA.Boxed (boxed struct receivers: an override (box + 4), ValueType.GetHashCode (the box), an interface call that mutates the box)', body: T('JitKA.Boxed(40)') },
  { name: 'JitKA.BoxedPoly / BoxedNull (two boxed structs and a class at one interface site, then runs of one class; a null receiver)', body: `${T('JitKA.BoxedPoly(48)')} ${T('JitKA.BoxedNull(null, 1)')} ${T('JitKA.BoxedNull(new JitKAC(), 2)')}` },
  { name: 'JitKA.Callee / Always (a newobj exit in a lazy callee, at the entry of every call)', body: `${T('JitKA.Callee(40)')} ${T('JitKA.Always(40)')}` },
  { name: 'JitKA.Chain5 (a lazy chain five deep, newobj exit at level 5)', body: T('JitKA.Chain5(40)') },
  { name: 'JitKA.Chain5Throw / CatchChain5 (DivideByZeroException at level 5 after a frame write; caught in the compiled caller)', body: `${T('JitKA.Chain5Throw(4)')} ${T('JitKA.Chain5Throw(5)')} ${T('JitKA.CatchChain5(8)')} ${T('JitKA.CatchChain5(9)')}` },
  { name: 'JitKA.ThrowAfterMat (the frame is written at the newobj exit, then the callee divides by zero)', body: `${T('JitKA.ThrowAfterMat(3)')} ${T('JitKA.ThrowAfterMat(4)')}` },
];
const KA_LIST = ['JitKA.*', 'JitKAS.*', 'JitKAN.*', 'JitKAC.*'];
// ---- end of the A1 block ----
// docs/207 §9 accessors (round 8 B6): [corpus name, JitAcc method, inputs, the method the compiler must list]
export const ACCESSORS: [string, string, number, string][] = [
  ['GameObject.GetComponent', 'GetComponentCase', 4, 'GameObject.GetComponent'],
  ['InteractionObjectPair.Equals', 'IopEqualsCase', 7, 'InteractionObjectPair.Equals'],
  ['GameObject.GetObject', 'GetObjectCase', 3, 'GameObject.GetObject'],
  ['SimClock.ElapsedTimeInMinutes', 'ElapsedMinutesCase', 35, 'SimClock.ElapsedTimeInMinutes'],
  ['GameObject.get_Parent', 'ParentCase', 2, 'GameObject.get_Parent'],
  ['ScriptObject.get_ObjectId', 'ObjectIdCase', 2, 'ScriptObject.get_ObjectId'],
  ['GameObject.get_LotCurrent', 'LotCurrentCase', 2, 'GameObject.get_LotCurrent'],
  ['Household.Members.GetActorsOfType', 'ActorsOfTypeCase', 5, 'Members.GetActorsOfType'],
  ['RoutingComponent.get_IsRouting', 'IsRoutingCase', 4, 'RoutingComponent.get_IsRouting'],
  ['Lot.get_CommercialLotSubType', 'LotSubTypeCase', 6, 'Lot.get_CommercialLotSubType'],
  ['ListSorted`1.GetEnumerator', 'ListSortedCase', 4, 'ListSorted`1.GetEnumerator'],
  ['DesireCurve.GetYFromX', 'DesireCurveCase', 12, 'DesireCurve.GetYFromX'],
  ['Sim.get_SimDescription', 'SimDescriptionCase', 2, 'Sim.get_SimDescription'],
  ['Lot.get_ShouldSimulate', 'ShouldSimulateCase', 4, 'Lot.get_ShouldSimulate'],
  ['SimDescription.get_Age', 'AgeCase', 5, 'SimDescription.get_Age'],
  ['Corners.get_Item', 'CornersCase', 5, 'Corners.get_Item'],
  ['Household.EveryHumanSimDescription', 'EveryHumanCase', 2, 'Household.EveryHumanSimDescription'],
];
const ACCESSOR_CASES: Case[] = ACCESSORS.map(([name, fn, n]) => ({ name: `Acc ${name}`, body: `for (int i = 0; i < ${n}; i++) ${T(`JitAcc.${fn}(i)`)}` }));
// ---- docs/537 §20 B3: the value-type field, element and object ops of B1 (MONOHOST_JITVTOPS=1): stfld.vt, ldelem.any.vt,
// stelem.any.vt, ldobj and stobj.vt over 1-, 7-, 12-, 20-byte structs and one holding references; null and bounds exits; generic
// T[] and ref T with T a struct, int, string or enum (only the struct forms compile, the rest stay EA's); List / Dictionary with
// struct keys and values. One block (A1 appends its own next to it).
const KB1 = `
    public struct JitB1 { public byte V; }
    public struct JitBOdd { public int A; public short B; public byte C; }
    public struct JitBRef { public string S; public int N; public object O; }
    public class JitKB
    {
        public JitTPt P; public JitB1 B; public JitBOdd D; public JitBRef R; public JitTBig G;
        static int H(int s, JitTPt p) { return s * 31 + p.Sum(); }
        static int H(int s, JitBOdd d) { return ((s * 31 + d.A) * 31 + d.B) * 31 + d.C; }
        static int H(int s, JitBRef r) { return ((s * 31 + r.N) * 31 + (r.S == null ? 1 : r.S.Length)) * 31 + (r.O == null ? 3 : 5); }
        public static int StFld(JitKB o, int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                JitTPt p = new JitTPt(i, -i, 3); o.P = p;
                JitB1 b; b.V = (byte)(i * 7); o.B = b;
                JitBOdd d; d.A = i; d.B = (short)(-i * 3); d.C = (byte)(i + 200); o.D = d;
                JitBRef r; r.S = (i & 1) == 0 ? "ab" : null; r.N = i; r.O = (i & 2) == 0 ? (object)o : null; o.R = r;
                JitTBig g; g.A = i; g.B = 1; g.C = 2; g.D = 3; g.E = -i; o.G = g;
                s = H(s, o.P) + o.B.V; s = H(s, o.D); s = H(s, o.R) + o.G.Sum();
            }
            return s;
        }
        public static int StFldNull(JitKB o, int k) { JitBOdd d; d.A = k; d.B = 1; d.C = 2; o.D = d; return k; }
        public static int Elem(int n)
        {
            JitTPt[] a = new JitTPt[5]; JitB1[] b = new JitB1[3]; JitBOdd[] d = new JitBOdd[4]; JitBRef[] r = new JitBRef[2]; JitTBig[] g = new JitTBig[2];
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                JitTPt p = new JitTPt(i, i + 1, -i); a[i % 5] = p; JitTPt q = a[(i * 3) % 5]; s = H(s, q);
                JitB1 x; x.V = (byte)i; b[i % 3] = x; x = b[(i + 1) % 3]; s += x.V;
                JitBOdd y; y.A = -i; y.B = (short)i; y.C = (byte)(i * 5); d[i & 3] = y; y = d[(i + 2) & 3]; s = H(s, y);
                JitBRef z; z.S = i % 3 == 0 ? null : "xyz"; z.N = i; z.O = a; r[i & 1] = z; z = r[(i + 1) & 1]; s = H(s, z);
                JitTBig w; w.A = i; w.B = i; w.C = i; w.D = i; w.E = 1; g[i & 1] = w; w = g[0]; s += w.Sum();
                a[0] = a[4]; d[1] = d[3];
            }
            return s;
        }
        public static int ElemLd(JitTPt[] a, int k) { JitTPt p = a[k]; return p.X; }
        public static int ElemSt(JitBOdd[] a, int k) { JitBOdd d; d.A = k; d.B = 2; d.C = 3; a[k] = d; return a.Length; }
        static void Swap(ref JitTPt x, ref JitTPt y) { JitTPt t = x; x = y; y = t; }
        static void SwapOdd(ref JitBOdd x, ref JitBOdd y) { JitBOdd t = x; x = y; y = t; }
        static void SwapRef(ref JitBRef x, ref JitBRef y) { JitBRef t = x; x = y; y = t; }
        static T Get<T>(ref T x) { return x; }
        static void Set<T>(ref T x, T v) { x = v; }
        public static int Refs(int n)
        {
            JitTPt a = new JitTPt(1, 2, 3), b = new JitTPt(4, 5, 6); JitBOdd c, d; c.A = 1; c.B = 2; c.C = 3; d.A = 4; d.B = 5; d.C = 6;
            JitTPt[] arr = new JitTPt[] { a, b }; JitKB o = new JitKB(), o2 = new JitKB(); o2.P = b;
            JitBRef e, f; e.S = "e"; e.N = 1; e.O = null; f.S = null; f.N = 2; f.O = o;
            int s = 0, iv = 0; string sv = null; JitEnI en = JitEnI.A; JitB1 b1; b1.V = 1;
            for (int i = 0; i < n; i++)
            {
                Swap(ref a, ref b); SwapOdd(ref c, ref d); SwapRef(ref e, ref f); Swap(ref arr[0], ref arr[1]); Swap(ref o.P, ref o2.P);
                s = H(s, a); s = H(s, c); s = H(s, e); s = H(s, arr[0]); s = H(s, o.P);
                Set(ref a, new JitTPt(i, 0, 1)); Set(ref b1, b1); b1.V++; Set(ref iv, i); Set(ref sv, (i & 1) == 0 ? "s" : null); Set(ref en, (JitEnI)(i & 7));
                s = H(s, Get(ref a)) + Get(ref b1).V + Get(ref iv) + (Get(ref sv) == null ? 1 : 2) + (int)Get(ref en); s = H(s, Get(ref c));
            }
            return s;
        }
        static T At<T>(T[] a, int i) { return a[i]; }
        static void Put<T>(T[] a, int i, T v) { a[i] = v; }
        public static int Gen(int n)
        {
            JitTPt[] a = new JitTPt[3]; int[] b = new int[3]; string[] c = new string[3]; JitEnB[] d = new JitEnB[3]; JitBOdd[] e = new JitBOdd[3];
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                Put(a, i % 3, new JitTPt(i, 1, 2)); Put(b, i % 3, i); Put(c, i % 3, (i & 1) == 0 ? "c" : null); Put(d, i % 3, (JitEnB)i);
                JitBOdd y; y.A = i; y.B = 3; y.C = 4; Put(e, i % 3, y);
                s = H(s, At(a, (i + 1) % 3)) + At(b, (i + 2) % 3) + (At(c, i % 3) == null ? 1 : 2) + (int)At(d, (i + 1) % 3); s = H(s, At(e, i % 3));
            }
            return s;
        }
        public static int GenBad(JitTPt[] a, int k) { Put(a, k, new JitTPt(k, k, k)); return At(a, k).X; }
        public static int Coll(int n)
        {
            List<JitTPt> l = new List<JitTPt>(); Dictionary<JitKey, JitTPt> d = new Dictionary<JitKey, JitTPt>(); Dictionary<int, JitBOdd> e = new Dictionary<int, JitBOdd>();
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                l.Add(new JitTPt(i, 2, 3)); if (l.Count > 7) l.RemoveAt(0); l[l.Count - 1] = new JitTPt(-i, i, 0);
                s = H(s, l[i % l.Count]); s += l.IndexOf(new JitTPt(-i, i, 0)) + l.IndexOf(new JitTPt(99, 0, 0));
                d[new JitKey(i % 11)] = new JitTPt(i, i, i); JitTPt v; if (d.TryGetValue(new JitKey((i * 7) % 13), out v)) s = H(s, v); else s += 5;
                if ((i & 3) == 0) d.Remove(new JitKey(i % 5));
                JitBOdd y; y.A = i; y.B = (short)i; y.C = 1; e[i % 6] = y; s = H(s, e[(i + 3) % 6 <= i ? (i + 3) % 6 : 0]);
            }
            foreach (KeyValuePair<JitKey, JitTPt> kv in d) s = H(s, kv.Value) + kv.Key.K;
            foreach (JitTPt p in l) s = H(s, p);
            return s;
        }
    }
`;
const VTOPS_CASES: Case[] = [
  { name: 'JitKB.StFld / StFldNull (B1 stfld.vt of 1-, 7-, 12-, 20-byte structs and one with references; a null receiver exits, EA throws)', body: `JitKB o = new JitKB(); ${T('JitKB.StFld(o, 40)')} ${T('JitKB.StFldNull(o, 3)')} ${T('JitKB.StFldNull(null, 4)')}` },
  { name: 'JitKB.Elem / ElemLd / ElemSt (B1 ldelem.any.vt / stelem.any.vt; null, -1 and the length exit, EA throws)', body: `JitTPt[] a = new JitTPt[3]; JitBOdd[] d = new JitBOdd[2]; ${T('JitKB.Elem(40)')} ${T('JitKB.ElemLd(a, 2)')} ${T('JitKB.ElemLd(a, 3)')} ${T('JitKB.ElemLd(a, -1)')} ${T('JitKB.ElemLd(null, 0)')} ${T('JitKB.ElemSt(d, 1)')} ${T('JitKB.ElemSt(d, 2)')} ${T('JitKB.ElemSt(d, -1)')} ${T('JitKB.ElemSt(null, 0)')}` },
  { name: 'JitKB.Refs (B1 ldobj / stobj.vt through ref locals, array elements, heap fields; generic ref T of int, string, enum stay EA\'s)', body: T('JitKB.Refs(40)') },
  { name: 'JitKB.Gen / GenBad (B1 generic T[] loads and stores of a struct, int, string, enum; bounds and null exits)', body: `JitTPt[] a = new JitTPt[2]; ${T('JitKB.Gen(40)')} ${T('JitKB.GenBad(a, 1)')} ${T('JitKB.GenBad(a, 2)')} ${T('JitKB.GenBad(null, 0)')}` },
  { name: 'JitKB.Coll (B1 List of a struct, Dictionary with colliding struct keys and struct values, Dictionary<int, 7-byte struct>, enumerators)', body: T('JitKB.Coll(60)') },
];
const VTOPS_LIST = ['JitKB.*'];
// handoff C2: the corpus fixtures of the C2 callees (the EA names are in kJitList; the fixture component overrides stand in for ObjectComponent.get_BaseType)
const C2_LIST = ['FxCompA.get_BaseType', 'FxCompB.get_BaseType', 'FxCompC.get_BaseType', 'FxProxy.get_ObjectId'];
// ---- docs/537 §20 B2b: newobj of a value type through the ctor's lazy body (MONOHOST_JITNEWVT=1): structs of 1, 7, 12, 20 bytes
// and one holding references; a ctor that leaves fields to `: this()`; ctors that throw (caught in the compiled caller and further
// out); newobj inside ctors and lazy chains 3+ deep; the result stored, passed, returned, boxed, put in arrays and fields;
// KeyValuePair, List / Dictionary enumerators (foreach) and ObjectGuid. Cases appended last (indices of older cases stay).
const KB2 = `
    public struct JitN1 { public byte V; public JitN1(int v) { V = (byte)(v * 3); } }
    public struct JitN7 { public int A; public short B; public byte C; public JitN7(int a, int b) { A = a; B = (short)b; C = (byte)(a ^ b); } }
    public struct JitN20 { public int A, B, C, D, E; public JitN20(int a) { A = a; B = a + 1; C = -a; D = a * a; E = 7; } public int Sum() { return A + B * 3 + C + D * 5 + E; } }
    public struct JitNRef { public string S; public int N; public object O; public JitNRef(string s, int n, object o) { S = s; N = n; O = o; } }
    public struct JitNPart { public int A, B; public long C; public JitNPart(int a) : this() { A = a; } }
    public struct JitNThr { public int V; public JitNThr(int k) { if (k < 0) throw new ArgumentException("k"); V = 100 / k; } }
    public struct JitNMid { public JitN7 In; public int K; public JitNMid(int k) { In = new JitN7(k, -k); K = new JitN1(k).V; } }
    public struct JitNOut { public JitNMid M; public JitTPt P; public JitNOut(int k) { M = new JitNMid(k); P = new JitTPt(k, M.K, M.In.A); } }
    public struct JitNDeep { public JitNOut O; public JitNThr T; public JitNDeep(int k) { O = new JitNOut(k); T = new JitNThr(k & 7); } }
    public struct JitNGuid { public ulong V; public JitNGuid(ulong v) { V = v; } }
    public class JitKN
    {
        public JitN7 F; public JitNRef R;
        static int H(int s, JitN7 d) { return ((s * 31 + d.A) * 31 + d.B) * 31 + d.C; }
        static int H(int s, JitNRef r) { return ((s * 31 + r.N) * 31 + (r.S == null ? 1 : r.S.Length)) * 31 + (r.O == null ? 3 : 5); }
        static int H(int s, JitNPart p) { return ((s * 31 + p.A) * 31 + p.B) * 31 + (int)p.C; }
        static int H(int s, JitNOut o) { return (H(s, o.M.In) + o.M.K) * 31 + o.P.X * 7 + o.P.Sum(); }
        static JitN7 Ret7(int k) { return new JitN7(k, k + 1); }
        static JitN20 Ret20(int k) { if (k > 100000) return new JitN20(0); return new JitN20(k); }
        static int Take(JitN7 d, JitN20 w) { return d.A + d.B + d.C + w.Sum(); }
        public static int Sizes(JitKN o, int n)
        {
            int s = 0; JitN7[] a = new JitN7[3]; JitNRef[] r = new JitNRef[2]; object box = null;
            for (int i = 0; i < n; i++)
            {
                JitN1 b = new JitN1(i); JitN7 d = new JitN7(i, -i); JitTPt p = new JitTPt(i, 2, -3); JitN20 w = new JitN20(i);
                s = H(s, d) + b.V + p.Sum() + w.Sum();
                o.F = new JitN7(i * 3, 9); a[i % 3] = new JitN7(-i, i); r[i & 1] = new JitNRef((i & 1) == 0 ? "ab" : null, i, (i & 2) == 0 ? (object)o : null);
                o.R = new JitNRef("x", -i, a); box = new JitN7(i, 1);
                s = H(s, o.F) + H(s, a[(i + 1) % 3]) + H(s, r[(i + 1) & 1]) + H(s, o.R) + ((JitN7)box).A;
                s += Take(new JitN7(i, 4), new JitN20(-i)) + Take(Ret7(i), Ret20(i)) + new JitN20(i + 1).Sum() + new JitN1(i + 2).V;
                JitNPart q = new JitNPart(i); s = H(s, q);
            }
            return s;
        }
        public static int Thr(int k) { JitNThr t = new JitNThr(k); return t.V + 1; }
        public static int CatchThr(int k) { try { return new JitNThr(k).V; } catch (DivideByZeroException) { return -1; } catch (ArgumentException) { return -2; } }
        static int D3(int k) { JitNDeep d = new JitNDeep(k); return H(0, d.O) + d.T.V; }
        static int D2(int k) { int t = D3(k); if (t > 1000000) return t; return t + new JitN7(k, 1).C; }
        static int D1(int k) { int t = D2(k); if (t > 1000000) return t; return t + new JitN1(k).V; }
        public static int Chain(int n) { int s = 0; for (int i = 1; i < n; i++) s = s * 31 + D1(i); return s; }
        public static int ChainThrow(int k) { return D1(k) + 1; }
        public static int Guid(ObjectGuid[] a, int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                ObjectGuid g = new ObjectGuid((ulong)(i * 0x100000001L)); JitNGuid h = new JitNGuid((ulong)i << 33);
                a[i % a.Length] = g; s = s * 31 + g.GetHashCode() + (int)(h.V >> 33) + (g == new ObjectGuid((ulong)(i * 0x100000001L)) ? 1 : 0);
                if (a[(i / 2) % a.Length].Equals(new ObjectGuid((ulong)((i / 2) * 0x100000001L)))) s += i;
            }
            return s;
        }
        public static int Coll(int n)
        {
            List<JitN7> l = new List<JitN7>(); Dictionary<int, JitN7> d = new Dictionary<int, JitN7>(); Dictionary<JitKey, string> e = new Dictionary<JitKey, string>();
            List<KeyValuePair<int, string>> kl = new List<KeyValuePair<int, string>>();
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                l.Add(new JitN7(i, 2)); if (l.Count > 6) l.RemoveAt(0); d[i % 9] = new JitN7(-i, i); e[new JitKey(i % 7)] = (i & 1) == 0 ? "e" : null;
                kl.Add(new KeyValuePair<int, string>(i, (i & 1) == 0 ? "k" : null)); if (kl.Count > 5) kl.RemoveAt(0);
                foreach (JitN7 x in l) s = H(s, x);
                foreach (KeyValuePair<int, JitN7> kv in d) s = H(s + kv.Key, kv.Value);
                foreach (KeyValuePair<JitKey, string> kv in e) s = s * 31 + kv.Key.K + (kv.Value == null ? 1 : 2);
                foreach (KeyValuePair<int, string> kv in kl) s = s * 31 + kv.Key + (kv.Value == null ? 3 : 4);
                KeyValuePair<JitKey, JitTPt> p = new KeyValuePair<JitKey, JitTPt>(new JitKey(i), new JitTPt(i, 1, 2)); s += p.Key.K + p.Value.Sum();
            }
            return s;
        }
    }
`;
const NEWVT_CASES: Case[] = [
  { name: 'JitKN.Sizes (B2b newobj of 1-, 7-, 12-, 20-byte structs and one with references; stored, passed, returned, boxed, in arrays and fields; a ctor with : this())', body: `JitKN o = new JitKN(); ${T('JitKN.Sizes(o, 40)')} ${T('JitKN.Sizes(null, 2)')}` },
  { name: 'JitKN.Thr / CatchThr (B2b a ctor that throws ArgumentException or DivideByZeroException; caught outside and in the compiled caller)', body: `${T('JitKN.Thr(5)')} ${T('JitKN.Thr(0)')} ${T('JitKN.Thr(-1)')} ${T('JitKN.CatchThr(4)')} ${T('JitKN.CatchThr(0)')} ${T('JitKN.CatchThr(-3)')}` },
  { name: 'JitKN.Chain / ChainThrow (B2b newobj inside ctors four deep under a lazy chain three deep; the innermost ctor divides by zero)', body: `${T('JitKN.Chain(24)')} ${T('JitKN.ChainThrow(8)')} ${T('JitKN.ChainThrow(9)')}` },
  { name: 'JitKN.Guid (B2b ObjectGuid and an ObjectGuid-like struct via newobj, compared and stored in an array; a Dictionary<ObjectGuid, int> made here gave one verify MISMATCH with NewVt on and off, fresh-object pairing)', body: `ObjectGuid[] a = new ObjectGuid[5]; ${T('JitKN.Guid(a, 40)')}` },
  { name: 'JitKN.Coll (B2b foreach over List / Dictionary: Enumerator and KeyValuePair newobj; KeyValuePair of struct key and value)', body: T('JitKN.Coll(30)') },
];
// ctors by full name: "Class.*" (JitListed) splits at the last dot, so it never matches "Class..ctor"
const NEWVT_LIST = ['JitKN.*', 'JitN20.*', 'JitN1..ctor', 'JitN7..ctor', 'JitN20..ctor', 'JitNRef..ctor', 'JitNPart..ctor', 'JitNThr..ctor', 'JitNMid..ctor', 'JitNOut..ctor', 'JitNDeep..ctor', 'JitNGuid..ctor', 'JitTPt..ctor', 'JitKey..ctor'];
// ---- handoff C3 (MONOHOST_JITFASTCOPY=1) and LDSFLDA (MONOHOST_JITLDSFLDA=1). C3: Array.Copy (in kJitList) calls the FastCopy
// icall: int, float, double, long, byte, enum, struct-without-references arrays (the inline memmove path), overlapping copies in
// one array, lengths 0 to 16, the 3-arg overload; reference, struct-with-references, widening, boxing, unboxing, enum-to-int and
// rank-2 copies (EA's other paths: the site exits); bounds, negative and null errors (managed Array.Copy throws).
// LDSFLDA: static struct fields read, written, called and passed by ref; reference statics;
// four holder classes per case with a static ctor: run r of a case (interpreted, interpreted, compiled, worker) touches holder
// min(r, 3) first, so the compiled run meets a class that is not initialised yet (class-init exit, EA runs the cctor).
const LSG = (p: string) => [0, 1, 2, 3].map(i => `    public class ${p}${i} { public static JitLsV V; public static JitLsR R; public static int N; public static long L; public static JitTPt P;
        static ${p}${i}() { V = new JitLsV(5); R.S = "g"; R.N = 2; N = 9; L = 0x100000003L; P = new JitTPt(1, 2, 3); JitLsRun.Inits++; } }`).join('\n');
const KC3 = `
    public struct JitC3P { public int A; public short B; public byte C; public JitC3P(int a) { A = a; B = (short)(a * 3); C = (byte)a; } }
    public struct JitC3R { public string S; public int N; }
    public enum JitC3E { A, B, C = 9 }
    public static class JitKC3
    {
        static int H(int s, int[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + a[i]; return s; }
        static int H(int s, long[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + (int)a[i] + (int)(a[i] >> 32); return s; }
        static int H(int s, float[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + (int)(a[i] * 8f); return s; }
        static int H(int s, double[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + (int)(a[i] * 16.0); return s; }
        static int H(int s, byte[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + a[i]; return s; }
        static int H(int s, JitC3P[] a) { for (int i = 0; i < a.Length; i++) s = ((s * 31 + a[i].A) * 31 + a[i].B) * 31 + a[i].C; return s; }
        static int H(int s, JitTPt[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + a[i].Sum() + a[i].X; return s; }
        static int H(int s, JitC3E[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + (int)a[i]; return s; }
        static int H(int s, object[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + (a[i] == null ? 1 : a[i] is string ? ((string)a[i]).Length : a[i].GetHashCode()); return s; }
        static int H(int s, JitC3R[] a) { for (int i = 0; i < a.Length; i++) s = (s * 31 + a[i].N) * 31 + (a[i].S == null ? 1 : a[i].S.Length); return s; }
        static int Try(Array a, int ai, Array b, int bi, int n) { try { Array.Copy(a, ai, b, bi, n); return 0; } catch (Exception e) { return e.GetType().Name.Length; } }
        public static int Ints(int n)
        {
            int s = 0; int[] a = new int[16], b = new int[16];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < 16; j++) a[j] = i * 17 + j;
                Array.Copy(a, i % 5, b, (i * 3) % 7, i % 9); s = H(s, b);
                Array.Copy(a, b, i % 17); s = H(s, b);
                Array.Copy(a, 0, b, 0, 16); Array.Copy(a, 16, b, 16, 0); s = H(s, b);
            }
            return s;
        }
        public static int Overlap(int n)
        {
            int s = 0; int[] a = new int[24]; byte[] c = new byte[24]; JitTPt[] p = new JitTPt[12];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < 24; j++) { a[j] = j * 7 + i; c[j] = (byte)(j + i); }
                for (int j = 0; j < 12; j++) p[j] = new JitTPt(j, i, -j);
                int k = i % 9;
                Array.Copy(a, 0, a, k, 12); s = H(s, a);
                Array.Copy(a, k, a, 0, 12); s = H(s, a);
                Array.Copy(c, 1, c, 0, 20); Array.Copy(c, 0, c, 3, 20); s = H(s, c);
                Array.Copy(p, 0, p, k % 4 + 1, 7); Array.Copy(p, k % 3, p, 0, 8); s = H(s, p);
                Array.Copy(a, 5, a, 5, 10); s = H(s, a);
            }
            return s;
        }
        public static int Kinds(int n)
        {
            int s = 0; float[] f = new float[9], f2 = new float[9]; double[] d = new double[9], d2 = new double[9]; long[] l = new long[9], l2 = new long[9];
            JitC3P[] q = new JitC3P[9], q2 = new JitC3P[9]; JitTPt[] t = new JitTPt[5], t2 = new JitTPt[5]; JitC3E[] e = new JitC3E[6], e2 = new JitC3E[6];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < 9; j++) { f[j] = j * 0.5f - i; d[j] = j * 0.25 + i; l[j] = ((long)i << 33) + j; q[j] = new JitC3P(i * 9 + j); }
                for (int j = 0; j < 5; j++) t[j] = new JitTPt(i, j, i - j);
                for (int j = 0; j < 6; j++) e[j] = (JitC3E)((i + j) % 3 == 2 ? 9 : (i + j) % 3);
                int k = i % 4, m = i % 6;
                Array.Copy(f, k, f2, 8 - m - k > 0 ? 1 : 0, m); Array.Copy(d, 0, d2, k, 9 - k); Array.Copy(l, k, l2, 0, 9 - k);
                Array.Copy(q, 1, q2, k, m); Array.Copy(t, 0, t2, k, 5 - k); Array.Copy(e, k, e2, 0, m);
                s = H(H(H(H(H(H(s, f2), d2), l2), q2), t2), e2);
            }
            return s;
        }
        // the rank-2 arrays come from the case body: one made here gives a verify MISMATCH in both arms (its bounds pointer, fresh-object pairing)
        public static int Others(int[,] g, int[,] g2, int n)
        {
            int s = 0; string[] a = new string[6], b = new string[6]; JitC3R[] r = new JitC3R[4], r2 = new JitC3R[4];
            int[] x = new int[6]; long[] w = new long[6]; JitC3E[] e = new JitC3E[6];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < 6; j++) { a[j] = (i + j) % 3 == 0 ? null : (j & 1) == 0 ? "s" : "tt"; x[j] = i * 6 + j; e[j] = (JitC3E)(j % 2); }
                for (int j = 0; j < 4; j++) { r[j].S = (j & 1) == 0 ? "r" : null; r[j].N = i + j; }
                g[i % 2, i % 3] = i;
                Array.Copy(a, 1, b, 0, 4); Array.Copy(r, 0, r2, 1, 3); s += Try(x, 0, w, 1, 5);
                s = H(H(H(s, b), r2), w);
                s += Try(e, 0, x, 0, 3); s += Try(g, 0, g2, 1, 5);
                s = H(s, x) + g2[1, 2] + g2[0, 1];
            }
            return s;
        }
        // object[] of boxed ints (filled by the case body) to int[] (EA's unboxing path), and int[] to object[] (boxing inside Array.Copy)
        public static int Box(object[] o, object[] o2, int n)
        {
            int s = 0; int[] x = new int[6];
            for (int i = 0; i < n; i++) { s += Try(o, i % 2, x, 0, 5); s = H(s, x); x[i % 6] = i; s += Try(x, 0, o2, i % 3, 3); s = H(s, o2); }
            return s;
        }
        public static int Bad(int k)
        {
            int[] a = new int[4], b = new int[4]; int[,] g = new int[2, 2]; string[] t = new string[] { "a", "b" }; object[] o = new object[] { 1, null };
            a[0] = 3; a[3] = 5;
            switch (k)
            {
                case 0: Array.Copy(a, 1, b, 0, 4); break;
                case 1: Array.Copy(a, 0, b, 2, 3); break;
                case 2: Array.Copy(a, -1, b, 0, 1); break;
                case 3: Array.Copy(a, 0, b, 0, -1); break;
                case 4: Array.Copy(null, 0, b, 0, 1); break;
                case 5: Array.Copy(a, 0, null, 0, 1); break;
                case 6: Array.Copy(t, 0, b, 0, 2); break;
                case 7: Array.Copy(g, 0, b, 0, 2); break;
                case 8: Array.Copy(o, 0, b, 0, 2); break;
                case 9: Array.Copy(a, 0, b, 4, 0); Array.Copy(a, 4, b, 0, 0); break;
                case 10: Array.Copy(a, b, 5); break;
            }
            return H(0, b);
        }
    }
    public struct JitLsV { public int X; public long T; public JitLsV(int x) { X = x; T = x * 1000L; } public int Sum() { return X + (int)(T >> 3); } public void Bump() { X++; T += 3; } }
    public struct JitLsR { public string S; public int N; }
    public static class JitLsRun { public static int Inits; public static int R0, W0; }
${LSG('JitLsG')}
${LSG('JitLsW')}
    public static class JitKLS
    {
        public static JitLsV SV; public static JitTPt SP;
        static int Add(ref int x, int d) { x += d; return x; }
        static long Mul(ref long x) { x = x * 3 + 1; return x; }
        static int Get(ref JitLsV v) { return v.Sum() + v.X; }
        static int HR(JitLsR r) { return r.N * 31 + (r.S == null ? 1 : r.S.Length); }
        public static int Read(int g)
        {
            switch (g)
            {
                case 0: return JitLsG0.V.X + JitLsG0.V.Sum() + (int)JitLsG0.V.T + JitLsG0.R.N + (JitLsG0.R.S == null ? 1 : JitLsG0.R.S.Length) + JitLsG0.P.Sum() + JitLsG0.P.Z + Get(ref JitLsG0.V);
                case 1: return JitLsG1.V.X + JitLsG1.V.Sum() + (int)JitLsG1.V.T + JitLsG1.R.N + (JitLsG1.R.S == null ? 1 : JitLsG1.R.S.Length) + JitLsG1.P.Sum() + JitLsG1.P.Z + Get(ref JitLsG1.V);
                case 2: return JitLsG2.V.X + JitLsG2.V.Sum() + (int)JitLsG2.V.T + JitLsG2.R.N + (JitLsG2.R.S == null ? 1 : JitLsG2.R.S.Length) + JitLsG2.P.Sum() + JitLsG2.P.Z + Get(ref JitLsG2.V);
                default: return JitLsG3.V.X + JitLsG3.V.Sum() + (int)JitLsG3.V.T + JitLsG3.R.N + (JitLsG3.R.S == null ? 1 : JitLsG3.R.S.Length) + JitLsG3.P.Sum() + JitLsG3.P.Z + Get(ref JitLsG3.V);
            }
        }
        // writes, then puts the cctor's values back, so a later run on the same holder starts from the same state
        public static int Write(int g, int k)
        {
            int s;
            switch (g)
            {
                case 0: JitLsW0.V.X += k; JitLsW0.V.Bump(); JitLsW0.R.S = k > 2 ? null : "w"; JitLsW0.R.N = k; s = Add(ref JitLsW0.N, k) + (int)Mul(ref JitLsW0.L) + Get(ref JitLsW0.V) + HR(JitLsW0.R); JitLsW0.P.Y = k; s += JitLsW0.P.Sum();
                    JitLsW0.V = new JitLsV(5); JitLsW0.R.S = "g"; JitLsW0.R.N = 2; JitLsW0.N = 9; JitLsW0.L = 0x100000003L; JitLsW0.P.Y = 2; return s;
                case 1: JitLsW1.V.X += k; JitLsW1.V.Bump(); JitLsW1.R.S = k > 2 ? null : "w"; JitLsW1.R.N = k; s = Add(ref JitLsW1.N, k) + (int)Mul(ref JitLsW1.L) + Get(ref JitLsW1.V) + HR(JitLsW1.R); JitLsW1.P.Y = k; s += JitLsW1.P.Sum();
                    JitLsW1.V = new JitLsV(5); JitLsW1.R.S = "g"; JitLsW1.R.N = 2; JitLsW1.N = 9; JitLsW1.L = 0x100000003L; JitLsW1.P.Y = 2; return s;
                case 2: JitLsW2.V.X += k; JitLsW2.V.Bump(); JitLsW2.R.S = k > 2 ? null : "w"; JitLsW2.R.N = k; s = Add(ref JitLsW2.N, k) + (int)Mul(ref JitLsW2.L) + Get(ref JitLsW2.V) + HR(JitLsW2.R); JitLsW2.P.Y = k; s += JitLsW2.P.Sum();
                    JitLsW2.V = new JitLsV(5); JitLsW2.R.S = "g"; JitLsW2.R.N = 2; JitLsW2.N = 9; JitLsW2.L = 0x100000003L; JitLsW2.P.Y = 2; return s;
                default: JitLsW3.V.X += k; JitLsW3.V.Bump(); JitLsW3.R.S = k > 2 ? null : "w"; JitLsW3.R.N = k; s = Add(ref JitLsW3.N, k) + (int)Mul(ref JitLsW3.L) + Get(ref JitLsW3.V) + HR(JitLsW3.R); JitLsW3.P.Y = k; s += JitLsW3.P.Sum();
                    JitLsW3.V = new JitLsV(5); JitLsW3.R.S = "g"; JitLsW3.R.N = 2; JitLsW3.N = 9; JitLsW3.L = 0x100000003L; JitLsW3.P.Y = 2; return s;
            }
        }
        // statics of the compiled class itself (no static ctor). A [ThreadStatic] field faults EA's interpreter in monohost (the
        // special-static lookup 0xEBAB30 reads a missing per-thread table), so the corpus has none; the compiler sends it to an exit
        public static int Own(int n)
        {
            int s = 0;
            for (int i = 0; i < n; i++)
            {
                SV.X = i; SV.Bump(); SP.X = i * 2; SP.Z = -i; s = s * 31 + Get(ref SV) + SP.Sum() + Add(ref SP.Y, 1);
            }
            SV = new JitLsV(0); SP = new JitTPt(0, 0, 0);
            return s;
        }
    }
`;
const C3_CASES: Case[] = [
  { name: 'JitKC3.Ints (C3 Array.Copy int[]: FastCopy memmove path, lengths 0 to 16, the 3-arg overload, zero-length copies at the end)', body: T('JitKC3.Ints(40)') },
  { name: 'JitKC3.Overlap (C3 overlapping copies in one int[], byte[], struct[]: forward, backward, same place)', body: T('JitKC3.Overlap(30)') },
  { name: 'JitKC3.Kinds (C3 float, double, long, struct-without-references, 12-byte struct and enum arrays)', body: T('JitKC3.Kinds(30)') },
  { name: 'JitKC3.Others (C3 string[], struct-with-references, int to long, enum to int, rank 2: EA\'s other paths, the site exits)', body: `int[,] g = new int[2, 3], g2 = new int[2, 3]; ${T('JitKC3.Others(g, g2, 20)')}` },
  { name: 'JitKC3.Box (C3 object[] of boxed ints to int[], int[] to object[]: EA\'s unboxing and boxing paths)', body: `object[] o = new object[6], o2 = new object[6]; for (int j = 0; j < 6; j++) o[j] = j * 5; ${T('JitKC3.Box(o, o2, 12)')}` },
  { name: 'JitKC3.Bad (C3 source / dest bounds, negative index and length, null source / dest, string to int, rank mismatch, null unboxing, empty copies at the end, 3-arg past the length)', body: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10].map(k => T(`JitKC3.Bad(${k})`)).join(' ') },
  { name: 'JitKLS.Read (LDSFLDA static struct fields read and called through their address; holder classes with a static ctor, the compiled run meets one not initialised yet)', body: `int r = JitLsRun.R0++; ${T('JitKLS.Read(r < 3 ? r : 3)')} ${T('JitKLS.Read(r < 3 ? r : 3)')}` },
  { name: 'JitKLS.Write (LDSFLDA stfld, by-ref int / long / struct args, reference field of a struct static; a fresh holder class per run, values put back)', body: `int r = JitLsRun.W0++; int g = r < 3 ? r : 3; ${T('JitKLS.Write(g, 1)')} ${T('JitKLS.Write(g, 4)')}` },
  { name: 'JitKLS.Own (LDSFLDA statics of the compiled class, no static ctor)', body: T('JitKLS.Own(20)') },
];
const C3_LIST = ['JitKC3.*', 'JitKLS.*'];
// handoff C5 (ClauseLazy): lazy bodies for methods with clauses. Compiled callers (JitKCL loops) call the 5-arg Array.Copy (two
// try / catch in its element loops) and our own try / catch / finally methods lazily; exceptions from a protected word, from a
// lazy callee inside and outside a try, caught, rethrown, through a finally, and the elements copied before Array.Copy throws
const KCL = `
    public static class JitKCL
    {
        public static int Fin;
        static int H(int s, object[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + (a[i] == null ? 1 : a[i] is string ? ((string)a[i]).Length + 7 : a[i].GetHashCode()); return s; }
        static int H(int s, int[] a) { for (int i = 0; i < a.Length; i++) s = s * 31 + a[i]; return s; }
        static int E(int s, Exception e) { string t = e.GetType().FullName + "|" + e.Message; for (int i = 0; i < t.Length; i++) s = s * 31 + t[i]; return s; }
        static int Thrower(int k) { if (k % 3 == 1) throw new ArgumentException("k" + k); return k * 2; }
        static int CatchIn(int[] a, int i) { try { return a[i] + Thrower(i); } catch (IndexOutOfRangeException) { return -1; } finally { Fin++; } }
        static int Outside(int[] a, int i) { int v = Thrower(i + 1); try { v += a[i]; } catch (NullReferenceException) { v = -2; } return v; }
        static int Rethrow(int i) { try { return Thrower(i); } catch (ArgumentException) { Fin += 10; throw; } }
        static int Filtered(int i) { try { return Thrower(i); } catch (InvalidOperationException) { return -3; } }
        // Array.Copy's slow loops: object[] holding a string to int[] (InvalidCastException from its catch), long[] to int[]
        // (ArrayTypeMismatchException), object[] to string[], int[] to long[] (widening, no throw), string[] to object[]
        public static int Copies(int n)
        {
            int s = 0; object[] o = new object[5], so = new object[4]; int[] x = new int[5]; long[] l = new long[5]; string[] t = new string[4];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < 5; j++) { o[j] = (i + j) % 4 == 3 ? (object)"s" : (object)(i * 5 + j); l[j] = i * 7 + j; x[j] = 0; }
                for (int j = 0; j < 4; j++) t[j] = (j & 1) == 0 ? "a" : null;
                try { Array.Copy(o, 0, x, 0, 5); } catch (Exception e) { s = E(s, e); }
                s = H(s, x);
                try { Array.Copy(l, 1, x, 0, 3 + i % 2); } catch (Exception e) { s = E(s, e); }
                try { Array.Copy(o, x, 4); } catch (Exception e) { s = E(s, e); }
                try { Array.Copy(o, 1, t, 0, 3); } catch (Exception e) { s = E(s, e); }
                try { Array.Copy(x, 0, l, 0, 5); Array.Copy(t, 0, so, 0, 4); Array.Copy(so, 1, so, 0, 3); } catch (Exception e) { s = E(s, e); }
                s = H(H(s, x), so) * 31 + (int)l[i % 5];
            }
            return s;
        }
        public static int User(int n)
        {
            int s = 0; int[] a = new int[4];
            Fin = 0;
            for (int i = 0; i < n; i++)
            {
                a[i % 4] = i;
                s = s * 31 + CatchIn(a, i % 6) + Fin;
                try { s = s * 31 + Outside(i % 5 == 4 ? null : a, i % 4); } catch (Exception e) { s = E(s, e); }
                try { s = s * 31 + Rethrow(i); } catch (Exception e) { s = E(s, e) + Fin; }
                try { s = s * 31 + Filtered(i); } catch (Exception e) { s = E(s, e); }
            }
            return s;
        }
    }
`;
const CL_CASES: Case[] = [
  { name: 'JitKCL.Copies (C5 Array.Copy try / catch: object[] with a string to int[], long[] to int[], object[] to string[], widening, references)', body: T('JitKCL.Copies(24)') },
  { name: 'JitKCL.User (C5 lazy callees with clauses: catch inside, throw outside a try, rethrow, finally, an uncaught type)', body: T('JitKCL.User(30)') },
];
const CL_LIST = ['JitKCL.*'];
export const cases = (): Case[] => [...kernelCases(), ...STMT_CASES, ...bclCases(), ...CLAUSE_CASES, ...LAZY_CASES, ...ACCESSOR_CASES, ...EXIT_CASES, ...VTOPS_CASES, ...NEWVT_CASES, ...C3_CASES, ...CL_CASES];

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
${KL.replace(/^\n|\n$/g, '')}
${KX.replace(/^\n|\n$/g, '')}
${KT.replace(/^\n|\n$/g, '')}
${KC5.replace(/^\n|\n$/g, '')}
${KA.replace(/^\n|\n$/g, '')}
${KB1.replace(/^\n|\n$/g, '')}
${KB2.replace(/^\n|\n$/g, '')}
${KC3.replace(/^\n|\n$/g, '')}
${KCL.replace(/^\n|\n$/g, '')}
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
        // identity hashes may differ between runs: a JitKO node hashes by its v, other reference types by type name
        static int M(int h, object v)
        {
            if (v == null) return M(h, 3);
            if (v is JitKO) return M(M(h, 7), ((JitKO)v).v);
            return M(h, v is string || v is ValueType ? v.GetHashCode() : v.GetType().FullName.GetHashCode());
        }
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
        // List state that a growth must keep: size, version, capacity, elements (the array object itself is not compared)
        static int ListState<T>(int h, List<T> l)
        {
            h = M(h, l.Count); h = M(h, l.Capacity);
            h = M(h, (int)typeof(List<T>).GetField("_version", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(l));
            foreach (T x in l) h = M(h, x == null ? 7 : x.GetHashCode());
            return h;
        }
        // docs/537 §15: a word in the list's graph names the dead, not yet swept arrays of the size the growth asks for. The verifier
        // takes them for objects, and the growth's allocation sweeps them and gets one. Addresses stay xored over the collection (under
        // verify an int[] is scanned, a plain address would keep them alive), and every earlier call's cells stay listed: which block
        // the sweep takes first is not known.
        static int[] s_dead = new int[600];
        static int s_deadN;
        static int ArrayBase(int[] a)
        {
            System.Runtime.InteropServices.GCHandle g = System.Runtime.InteropServices.GCHandle.Alloc(a, System.Runtime.InteropServices.GCHandleType.Pinned);
            int p = (int)g.AddrOfPinnedObject() - 0x10; g.Free();
            return p;
        }
        static int ListGrowDead(int h, int fill)
        {
            const int N = 60, X = 0x55555554;
            int[][] keep = new int[N][];  // a live cell in every block: Boehm frees an all-dead block at once, only a mixed block waits for the sweep
            for (int i = 0; i < N; i++)
            {
                keep[i] = new int[N * 2];
                s_dead[s_deadN++ % s_dead.Length] = ArrayBase(new int[N * 2]) ^ X;
            }
            GC.Collect();
            int[] idx = new int[s_dead.Length];
            for (int i = 0; i < idx.Length; i++) idx[i] = s_dead[i] ^ X;
            for (int i = 0; i < fill; i++) s_dead[s_deadN++ % s_dead.Length] = ArrayBase(new int[N * 2]) ^ X;  // drain the swept cells: the growth must be the allocation that sweeps
            List<int> l = new List<int>(N);
            l.Add(ArrayBase(idx));
            for (int i = 1; i < N; i++) l.Add(i);
            l.Add(-1);
            s_dead[s_deadN++ % s_dead.Length] = ArrayBase((int[])typeof(List<int>).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(l)) ^ X;
            h = M(h, l.Count); h = M(h, l.Capacity); h = M(h, l[N - 1]); h = M(h, l[N]); h = M(h, keep[N - 1].Length);
            return h;
        }
        // every way into GrowIfNeeded: new list (EmptyArray -> 4), doubling, a capacity argument, AddRange past double, Insert
        static int ListGrowth<T>(int h, T[] v)
        {
            for (int c = 0; c < 3; c++)
            {
                List<T> l = c == 0 ? new List<T>() : new List<T>(c * 3 - 2); h = ListState(h, l);
                for (int i = 0; i < 70; i++) { l.Add(v[i % v.Length]); h = M(h, l.Capacity); }
                h = ListState(h, l);
                l.AddRange(new List<T>(v)); l.AddRange(l.ToArray()); h = ListState(h, l);
                l.Insert(1, v[0]); h = ListState(h, l);
                l.Capacity = l.Count; l.Add(v[1]); h = ListState(h, l);
            }
            return h;
        }
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
  return [...m[1].replace(/\/\/[^\n]*/g, '').matchAll(/"([^"]+)"/g)].map(x => x[1]).concat('JitK.*', 'JitKO.*', 'JitKL.*', 'JitKLPt.*', 'JitKV.*', 'JitKV2.*', 'JitKV3.*', 'JitKX.*', 'JitKT.*', 'JitKTF.*', 'JitKCV.*', 'JitKI.*', 'JitKIN.*', 'JitIN.*', 'JitINS.*', 'JitIND.*', 'JitC5A.*', 'JitRKey.*', 'JitRKey2.*', 'JitTPt.*', 'JitTBig.*', ...ACCESSORS.map(a => a[3]), ...KA_LIST, ...VTOPS_LIST, ...C2_LIST, ...NEWVT_LIST, ...C3_LIST, ...CL_LIST).join(',');
}

// ---- report ----
export type Report = { cases: number; differ: number; unstable: number; traceOnly: number; lines: string[]; verify?: string; superops: string[]; missingSuper: string[] };
// EA's trace printer (0xE52A0F) prints offset 0 and "superop" for a frame whose ip word is a superop (>= 0x1AA); a compiled
// frame (JIT_OP 0x1A9 there) prints the true offset. Trails equal but for such frames: the case differs in stack-trace text only.
export function traceOnly(interp: string, comp: string): boolean {
  if (interp.length >= 1100 || comp.length >= 1100) return false;  // the C# trail stops at 1100 chars
  const seg = (t: string) => t.split(' | ').filter(s => s.trim()).map(s => /^(\S+) #0: 0x([0-9a-f]+)\s+(?:(\S+)\s+)?in (.+?\(.*?\))/.exec(s.trim()));
  const a = seg(interp), b = seg(comp);
  let used = false;
  const same = a.length === b.length && a.every((x, i) => {
    const y = b[i];
    if (!x || !y || x[1] !== y[1] || x[4] !== y[4]) return false;
    if (x[2] === y[2]) return true;
    return used = x[3] === 'superop' && x[2] === '00000';
  });
  return same && used;
}
export function report(log: string, dump: string | undefined, names: string[]): Report {
  const r: Report = { cases: 0, differ: 0, unstable: 0, traceOnly: 0, lines: [], superops: [], missingSuper: [] };
  const trails: Record<string, Record<string, string>> = {}, mism: [number, string][] = [];
  for (const l of log.split(/\r?\n/)) {
    let m = /^jit corpus: (\d+) cases, (\d+) differ, (\d+) unstable;(.*)$/.exec(l);
    if (m) { r.cases = +m[1]; r.differ = +m[2]; r.unstable = +m[3]; r.verify = m[4].trim(); continue; }
    m = /^jit corpus (MISMATCH|UNSTABLE|FAULT) case (\d+): (.*)$/.exec(l);
    if (m) { if (m[1] === 'MISMATCH') mism.push([r.lines.length, m[2]]); r.lines.push(`${m[1]} ${m[2]} ${names[+m[2]] ?? '?'}: ${m[3]}`); continue; }
    m = /^jit corpus trail case (\d+) (interpreted|compiled): (.*)$/.exec(l);
    if (m) { (trails[m[1]] ??= {})[m[2]] = m[3]; r.lines.push(`    ${m[2]}: ${m[3].slice(0, 400)}`); }
  }
  // values: the per-call verifier compared every compiled call that returned; only then is a trace-only label safe
  if (/MISMATCH 0,/.test(r.verify ?? '')) for (const [i, c] of mism) {
    const t = trails[c];
    if (t?.interpreted !== undefined && t.compiled !== undefined && traceOnly(t.interpreted, t.compiled)) {
      r.lines[i] = r.lines[i].replace(/^MISMATCH/, 'MISMATCH (stack-trace text only: EA prints offset 0 at a superop)');
      r.traceOnly++;
    }
  }
  if (dump) {  // superops seen in the methods the corpus compiled (mint listing, then "compiled:" when P0 compiled it)
    const seen = new Set<string>();
    // lazy frames compile a callee while its caller's block is still open (caller listing, callee block, callee "compiled:",
    // caller "compiled:"), so a "compiled:" line belongs to the innermost block that has no compile line yet
    const open: string[][] = [];
    for (const l of dump.split(/\r?\n/)) {
      if (l.startsWith('== ')) { open.push([]); continue; }
      if (/^\s+compiled:/.test(l)) { open.pop()?.forEach(o => seen.add(o)); continue; }
      const m = /^\s+[0-9A-F]{4} super_(1[A-C][0-9A-F])/.exec(l);
      if (m) open[open.length - 1]?.push(m[1]);
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
  ok(jitList(src) === 'A.B,C`2.D,E.F,JitK.*,JitKO.*,JitKL.*,JitKLPt.*,JitKV.*,JitKV2.*,JitKV3.*,JitKX.*,JitKT.*,JitKTF.*,JitKCV.*,JitKI.*,JitKIN.*,JitIN.*,JitINS.*,JitIND.*,JitC5A.*,JitRKey.*,JitRKey2.*,JitTPt.*,JitTBig.*,' + ACCESSORS.map(a => a[3]).join(',') + ',' + [...KA_LIST, ...VTOPS_LIST, ...C2_LIST, ...NEWVT_LIST, ...C3_LIST, ...CL_LIST].join(','), 'kJitList parse');
  const live = jitList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8'));
  ok(live.split(',').length <= 256 && live.length < 8192, 'compile list fits monohost (g_jitNames[256], g_jitListBuf[8192])');
  ok(new Set(ACCESSORS.map(a => a[1])).size === ACCESSORS.length && ACCESSORS.every(a => /^\w+Case$/.test(a[1]) && a[2] > 0), 'accessor table');
  const log = 'jit corpus MISMATCH case 1: compiled 0000002A interpreted 0000002B\njit corpus trail case 1 compiled: DivideByZeroException at X | \njit corpus UNSTABLE case 2: interpreted 1 then 2\n'
    + 'jit corpus: 3 cases, 1 differ, 1 unstable; per-call verify during the corpus: ok 5, MISMATCH 0, abandoned (exception) 2\n';
  const dump = '== MonoHostTest.JitKO.S1AB rm 0 words 3\n  0000 super_1AB        0000\n   compiled: 2 positions\n== Other.M rm 0\n  0000 super_1AD\n';
  const r = report(log, dump, ['a', 'b', 'c']);
  ok(r.cases === 3 && r.differ === 1 && r.unstable === 1 && r.lines[0] === 'MISMATCH 1 b: compiled 0000002A interpreted 0000002B'
    && r.lines[1] === '    compiled: DivideByZeroException at X | ', 'log parse');
  ok(r.superops.join() === '1AB' && r.missingSuper.length === 21 && !r.missingSuper.includes('1AB'), 'superop coverage from the dump');
  const nested = '== MonoHostTest.JitKO.S1B4 rm 0\n  0014 super_1B4\n== MonoHostTest.JitKO.Same rm 0\n  0000 ldarg.0\n   compiled: 3 positions\n   compiled: 15 positions\n';
  ok(report(log, nested, ['a', 'b', 'c']).superops.join() === '1B4', 'superop coverage with a lazy callee compiled inside the caller');
  const fi = 'NullReferenceException #0: 0x00000 superop    in N.K:S (N.K,bool) ([0CED8F20] [0] ) | ', fc = 'NullReferenceException #0: 0x00010            in N.K:S (N.K,bool) ([0CED8E60] [0] ) | ';
  ok(traceOnly(fi, fc) && !traceOnly(fc, fc) && !traceOnly(fi.replace('0x00000 superop', '0x00004 ldfld.o'), fc), 'trace-only rule (superop offset)');
  console.log(`jit-corpus self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else if (process.argv.includes('--write')) { const g = generate(); writeFileSync(OUT, g); console.log(`wrote ${OUT}: ${cases().length} cases, ${K.length} arithmetic kernels`); }
  else if (process.argv.includes('--env')) console.log(jitList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8')));
  else if (arg('--index')) console.log(cases().findIndex(c => c.name.startsWith(arg('--index')!)));
  else if (arg('--report')) {
    const d = arg('--dump'), r = report(readFileSync(arg('--report')!, 'utf8'), d ? readFileSync(d, 'utf8') : undefined, cases().map(c => c.name));
    if (!r.cases) { console.log('jit-corpus: no "jit corpus:" summary line (MONOHOST_JITCORPUS=1 set? MonoHostTest.dll built with JitCorpus.cs?)'); process.exitCode = 1; }
    else console.log(`jit-corpus: ${r.cases} cases, ${r.differ} differ${r.traceOnly ? ` (${r.traceOnly} in stack-trace text only)` : ''}, ${r.unstable} unstable; ${r.verify}`);
    for (const l of r.lines) console.log(`  ${l}`);
    if (d) console.log(`superops in the compiled methods: ${r.superops.length} of 22${r.missingSuper.length ? ` (missing ${r.missingSuper.join(' ')})` : ''}`);
    const review = r.lines.length - 3 * r.traceOnly + (r.cases ? 0 : 1);  // a trace-only mismatch and its two trail lines
    console.log(`needs review: ${review ? `${review} item(s) above` : 'none'}`);
  } else console.log('usage: --write | --env | --index <name prefix> | --report <monohost.exe.log> [--dump <jit-dump.txt>] | --self-check');
}
