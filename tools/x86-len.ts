// x86-32 instruction length decoder (table-driven): length plus where the ModRM displacement, the immediate and a
// relative branch operand sit. Used to diff two copies of the same code field by field (tools/exe-port-diff.ts).
//
//   node tools/x86-len.ts --self-check
//
// shortcut: 32-bit mode only, no VEX/EVEX (the 2009-era TS3W.exe has none); 0x67 uses 16-bit addressing.
import { fileURLToPath } from "node:url";

export type Ins = {
    len: number; op: number;              // op: 0xXX, 0x0FXX, 0x0F38XX, 0x0F3AXX
    dispOff: number; dispSize: number;    // offsets relative to the instruction start; size 0 = none
    abs: boolean;                         // displacement is an absolute address (no base register, or moffs)
    immOff: number; immSize: number;
    relOff: number; relSize: number;      // jcc/jmp/call/loop operand
};

// . none, m ModRM, b imm8, z imm16/32, w imm16, M ModRM+imm8, Z ModRM+imm16/32, r rel8, R rel16/32, e enter,
// f far pointer, o moffs, g F6 / G F7 (immediate only for /0 and /1), p prefix, x invalid, 2 0F, 3 0F38, A 0F3A
const ONE = [
    "mmmmbz..mmmmbz.2", "mmmmbz..mmmmbz..", "mmmmbzp.mmmmbzp.", "mmmmbzp.mmmmbzp.",
    "................", "................", "..mmppppzZbM....", "rrrrrrrrrrrrrrrr",
    "MZMMmmmmmmmmmmmm", "..........f.....", "oooo....bz......", "bbbbbbbbzzzzzzzz",
    "MMw.mmMZe.w..b..", "mmmmbb..mmmmmmmm", "rrrrbbbbRRfr....", "p.pp..gG......mm"].join("");
const TWO = [
    "mmmmx.....x.xm.M", "mmmmmmmmmmmmmmmm", "mmmmxxxxmmmmmmmm", "......x.3xAxxxxx",
    "mmmmmmmmmmmmmmmm", "mmmmmmmmmmmmmmmm", "mmmmmmmmmmmmmmmm", "MMMMmmm.mmxxmmmm",
    "RRRRRRRRRRRRRRRR", "mmmmmmmmmmmmmmmm", "...mMmxx...mMmmm", "mmmmmmmmmmMmmmmm",
    "mmMmMMMm........", "mmmmmmmmmmmmmmmm", "mmmmmmmmmmmmmmmm", "mmmmmmmmmmmmmmmm"].join("");
const PREFIX = new Set([0x66, 0x67, 0xf0, 0xf2, 0xf3, 0x26, 0x2e, 0x36, 0x3e, 0x64, 0x65]);

// null = invalid opcode or runs past `end`
export function decode(b: Uint8Array, at: number, end = b.length): Ins | null {
    let p = at, opsz = false, adsz = false;
    while (p < end && p - at < 14 && PREFIX.has(b[p])) { if (b[p] === 0x66) opsz = true; if (b[p] === 0x67) adsz = true; p++; }
    if (p >= end) return null;
    let op = b[p++], t = ONE[op];
    if (t === "2") {
        if (p >= end) return null;
        const c = b[p++]; t = TWO[c]; op = 0x0f00 | c;
        if (t === "3" || t === "A") { if (p >= end) return null; op = (t === "3" ? 0x0f3800 : 0x0f3a00) | b[p++]; t = t === "3" ? "m" : "M"; }
    }
    if (t === "x" || t === "p") return null;
    const z = opsz ? 2 : 4;
    const ins: Ins = { len: 0, op, dispOff: 0, dispSize: 0, abs: false, immOff: 0, immSize: 0, relOff: 0, relSize: 0 };
    let reg = 0;
    if ("mMZgG".includes(t)) {
        if (p >= end) return null;
        const modrm = b[p++], mod = modrm >> 6, rm = modrm & 7;
        reg = (modrm >> 3) & 7;
        if (mod !== 3) {
            if (adsz) ins.dispSize = mod === 0 && rm === 6 ? (ins.abs = true, 2) : mod === 1 ? 1 : mod === 2 ? 2 : 0;
            else {
                let base = rm;
                if (rm === 4) { if (p >= end) return null; base = b[p++] & 7; }
                ins.dispSize = mod === 0 && base === 5 ? (ins.abs = true, 4) : mod === 1 ? 1 : mod === 2 ? 4 : 0;
            }
            if (ins.dispSize) { ins.dispOff = p - at; p += ins.dispSize; }
        }
    }
    const imm = { b: 1, M: 1, z, Z: z, w: 2, e: 3, f: z + 2, g: reg < 2 ? 1 : 0, G: reg < 2 ? z : 0 }[t as "b"] ?? 0;
    if (t === "o") { ins.dispOff = p - at; ins.dispSize = adsz ? 2 : 4; ins.abs = true; p += ins.dispSize; }
    if (imm) { ins.immOff = p - at; ins.immSize = imm; p += imm; }
    if (t === "r" || t === "R") { ins.relOff = p - at; ins.relSize = t === "r" ? 1 : z; p += ins.relSize; }
    if (p > end) return null;
    ins.len = p - at;
    return ins;
}

// field value: displacement signed, immediate unsigned
export function field(b: Uint8Array, at: number, size: number, signed: boolean): number {
    let v = 0;
    for (let i = size - 1; i >= 0; i--) v = v * 256 + b[at + i];
    return signed && v >= 2 ** (8 * size - 1) ? v - 2 ** (8 * size) : v;
}

function selfCheck() {
    const cases: [string, Partial<Ins>][] = [
        ["8B 81 18 09 00 00", { len: 6, dispOff: 2, dispSize: 4, abs: false }],       // mov eax,[ecx+0x918]
        ["8B 44 24 08", { len: 4, dispOff: 3, dispSize: 1 }],                          // mov eax,[esp+8]
        ["C7 46 10 01 00 00 00", { len: 7, dispOff: 2, dispSize: 1, immOff: 3, immSize: 4 }],
        ["E8 00 00 00 00", { len: 5, relOff: 1, relSize: 4 }],
        ["0F 84 10 00 00 00", { len: 6, op: 0x0f84, relOff: 2, relSize: 4 }],
        ["66 C7 40 04 34 12", { len: 6, dispSize: 1, immOff: 4, immSize: 2 }],
        ["A1 20 B9 1C 01", { len: 5, dispOff: 1, dispSize: 4, abs: true }],
        ["8B 0D 20 B9 1C 01", { len: 6, dispOff: 2, dispSize: 4, abs: true }],
        ["8B 04 85 00 10 40 00", { len: 7, dispOff: 3, dispSize: 4, abs: true }],     // [eax*4+disp32]
        ["8B 44 85 08", { len: 4, dispOff: 3, dispSize: 1, abs: false }],             // [ebp+eax*4+8]
        ["F7 C1 00 01 00 00", { len: 6, immOff: 2, immSize: 4 }],
        ["F7 D8", { len: 2, immSize: 0 }],
        ["83 C4 0C", { len: 3, immOff: 2, immSize: 1 }],
        ["0F B6 41 05", { len: 4, dispOff: 3, dispSize: 1 }],
        ["F3 0F 10 44 24 04", { len: 6, dispOff: 5, dispSize: 1 }],
        ["66 0F 3A 0F C1 08", { len: 6, op: 0x0f3a0f, immOff: 5, immSize: 1 }],
        ["C2 08 00", { len: 3, immSize: 2 }],
        ["D9 45 08", { len: 3, dispSize: 1 }],
        ["EB FE", { len: 2, relOff: 1, relSize: 1 }],
        ["C8 10 00 00", { len: 4, immSize: 3 }],
        ["64 A1 18 00 00 00", { len: 6, dispOff: 2, abs: true }],
    ];
    for (const [hex, want] of cases) {
        const b = Uint8Array.from(hex.split(" ").map((h) => parseInt(h, 16)));
        const got = decode(b, 0);
        for (const [k, v] of Object.entries(want)) if (!got || (got as any)[k] !== v) { console.error(`x86-len self-check FAILED: ${hex} ${k} = ${got && (got as any)[k]}, want ${v}`); process.exit(1); }
    }
    if (decode(Uint8Array.from([0x8b, 0x81, 0x18]), 0) !== null) { console.error("x86-len self-check FAILED: truncated not null"); process.exit(1); }
    if (field(Uint8Array.from([0xfc]), 0, 1, true) !== -4 || field(Uint8Array.from([0x18, 0x09, 0, 0]), 0, 4, true) !== 0x918) { console.error("x86-len self-check FAILED: field"); process.exit(1); }
    console.log(`x86-len self-check: ${cases.length} encodings ok`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    if (process.argv.includes("--self-check")) selfCheck();
    else { console.error("usage: node tools/x86-len.ts --self-check (library for tools/exe-port-diff.ts)"); process.exit(2); }
}
