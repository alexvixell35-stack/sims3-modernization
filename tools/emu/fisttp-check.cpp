// docs/221 §5: fisttp qword against EA's two forms on this CPU: fnstcw/or 0xC00/fldcw/fistp q/fldcw (conv.u4.r8) and the same
// result _ftol2 returns (conv.i8/u8.r8, EA's CRT does exactly that truncation for doubles [docs/221 row]). Compares result,
// FSW exception flags and x87 stack top. 32-bit x86 only.
#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <intrin.h>
#include <windows.h>
static __int64 r1, r2;
static unsigned short sw1, sw2, cw0, cwT;
static double gin;
static void Run() {
    __asm {
        fnclex
        fld qword ptr gin
        fnstcw cw0
        mov ax, cw0
        or ax, 0xC00
        mov cwT, ax
        fldcw cwT
        fistp qword ptr r1
        fldcw cw0
        fnstsw sw1
        fnclex
        fld qword ptr gin
        lea eax, r2
        _emit 0xDD
        _emit 0x08
        fnstsw sw2
        fnclex
    }
}
static uint64_t rng = 0x9E3779B97F4A7C15ull;
static uint64_t Next() { rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17; return rng; }
int main() {
    int info[4]; __cpuid(info, 1);
    char brand[49] = {}; __cpuid((int*)brand, 0x80000002); __cpuid((int*)(brand + 16), 0x80000003); __cpuid((int*)(brand + 32), 0x80000004);
    printf("cpu %s, SSE3 %d\n", brand, info[2] & 1);
    if (!(info[2] & 1)) return 2;
    static const double two63 = 9223372036854775808.0, two64 = 18446744073709551616.0, two31 = 2147483648.0, two32 = 4294967296.0;
    uint64_t special[] = {0x7FF0000000000000ull, 0xFFF0000000000000ull, 0x7FF8000000000000ull, 0xFFF8000000000000ull, 0x7FF0000000000001ull, 0xFFF0000000000001ull, 0x7FFFFFFFFFFFFFFFull,
                          0, 0x8000000000000000ull, 1, 0x8000000000000001ull, 0x000FFFFFFFFFFFFFull, 0x7FEFFFFFFFFFFFFFull, 0xFFEFFFFFFFFFFFFFull};
    uint64_t n = 0, bad = 0, nInd = 0;
    auto one = [&](double d) {
        gin = d; r1 = r2 = 0x5A5A5A5A5A5A5A5All;
        Run();
        n++;
        if ((uint64_t)r1 == 0x8000000000000000ull) nInd++;
        if (r1 != r2 || (sw1 & 0x1F) != (sw2 & 0x1F) || ((sw1 ^ sw2) & 0x3800)) {
            if (bad++ < 20) printf("DIFF %016llX %.17g: fistp %016llX sw %04X, fisttp %016llX sw %04X\n", *(uint64_t*)&d, d, r1, sw1, r2, sw2);
        }
    };
    for (uint64_t s : special) one(*(double*)&s);
    const double near_[] = {two63, -two63, two64, -two64, two31, -two31, two32, -two32};
    for (double b : near_) {
        uint64_t u = *(uint64_t*)&b;
        for (int k = -4096; k <= 4096; k++) { uint64_t v = u + k; one(*(double*)&v); }
        static const double fr[] = {-1.5, -1.0, -0.5, 0.5, 1.0, 1.5}; for (double f : fr) one(b + f);
    }
    for (int i = -200000; i <= 200000; i++) one(i / 8.0);
    for (uint64_t i = 0; i < 300000000ull; i++) { uint64_t v = Next(); one(*(double*)&v); }  // every exponent
    for (uint64_t i = 0; i < 300000000ull; i++) {  // the integer range
        uint64_t v = Next(); v = (v & 0x800FFFFFFFFFFFFFull) | ((uint64_t)(1023 + (Next() % 66)) << 52); one(*(double*)&v);
    }
    printf("%llu inputs, %llu indefinite results, %llu differ\n", n, nInd, bad);
    return bad != 0;
}
