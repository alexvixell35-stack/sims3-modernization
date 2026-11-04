// Field-level diffs between two TS3W.exe builds, used by tools/exe-addr-map.ts (docs/68 §7.2):
//  - struct offset diff: instructions of a matched window decoded in both copies (tools/x86-len.ts); a displacement
//    or immediate that differs (`[reg+0x918]` -> `[reg+0x91C]`) is a shifted struct field or a changed constant;
//  - fuzzy mask: the same window with every displacement, immediate and branch operand wildcarded, so a function whose
//    only change is a field offset still matches (kind `offset-changed` in the address map);
//  - vtable slot map: vtables (runs of relocated pointers into code outside code sections) in both exes, mapped by
//    majority vote over their functions; slot shifts per vtable.
//
//   node tools/exe-port-diff.ts --self-check
//
// shortcut: a displacement that changes size (disp8 0x7C -> disp32 0x80) changes the instruction length and is
// not matched by the fuzzy mask; such sites stay `missing`.
// shortcut: vtables need >= 2 slots and are found by relocs only (no RTTI); function identity = unique masked
// 24-byte prologue among vtable targets plus the address map's own rows.
import { fileURLToPath } from "node:url";
import { decode, field } from "./x86-len.ts";

export type Pe = { base: number; img: Buffer; exec: [number, number][]; mask: Uint8Array; relocs: Set<number> };

// instruction starts in [lo, hi), synced on `anchor` (an instruction start): the earliest start in [lo, anchor]
// whose decode chain lands exactly on anchor, then on to hi
export function insStarts(img: Uint8Array, lo: number, hi: number, anchor: number): number[] {
    anchor = Math.max(lo, Math.min(anchor, hi));
    const reach = new Uint8Array(anchor - lo + 1);
    reach[anchor - lo] = 1;
    let first = anchor;
    for (let s = anchor - 1; s >= lo; s--) {
        const d = decode(img, s, hi);
        if (d && s + d.len <= anchor && reach[s + d.len - lo]) { reach[s - lo] = 1; first = s; }
    }
    const out: number[] = [];
    for (let p = first; p < hi;) { const d = decode(img, p, hi); if (!d) break; out.push(p); p += d.len; }
    return out;
}

// the ref mask over [lo, hi) plus every displacement, immediate and branch operand of the decoded instructions
export function fuzzyMask(r: Pe, lo: number, hi: number, anchor: number): Uint8Array {
    const m = r.mask.slice(lo, hi);
    for (const s of insStarts(r.img, lo, hi, anchor)) {
        const d = decode(r.img, s, hi)!;
        for (const [o, n] of [[d.dispOff, d.dispSize], [d.immOff, d.immSize], [d.relOff, d.relSize]]) if (n) m.fill(1, s + o - lo, s + o + n - lo);
    }
    return m;
}

export type FieldDiff = { at: number; kind: "disp" | "imm"; old: number; new: number; size: number };

// displacements (not absolute: those are pointers, mapped as refs) and immediates (not relocated) that differ between
// ref [lo, hi) and other [lo + delta, hi + delta); stops at the first instruction that decodes differently
export function fieldDiffs(r: Pe, o: Pe, lo: number, hi: number, delta: number, anchor: number): FieldDiff[] {
    const out: FieldDiff[] = [];
    for (const s of insStarts(r.img, lo, hi, anchor)) {
        const d = decode(r.img, s, hi)!, e = decode(o.img, s + delta, hi + delta);
        if (!e || e.len !== d.len || e.op !== d.op || e.dispSize !== d.dispSize || e.immSize !== d.immSize) break;
        if (d.dispSize && !d.abs && !r.mask[s + d.dispOff]) {
            const a = field(r.img, s + d.dispOff, d.dispSize, true), b = field(o.img, s + delta + d.dispOff, d.dispSize, true);
            if (a !== b) out.push({ at: r.base + s, kind: "disp", old: a, new: b, size: d.dispSize });
        }
        if (d.immSize && !r.mask[s + d.immOff]) {
            const a = field(r.img, s + d.immOff, d.immSize, false), b = field(o.img, s + delta + d.immOff, d.immSize, false);
            const ptr = (pe: Pe, v: number) => v >= pe.base && v < pe.base + pe.img.length;  // `push offset x`: a ref, not a field
            if (a !== b && !(d.immSize === 4 && ptr(r, a) && ptr(o, b))) out.push({ at: r.base + s, kind: "imm", old: a, new: b, size: d.immSize });
        }
    }
    return out;
}

const sx = (v: number) => (v < 0 ? "-0x" + (-v).toString(16).toUpperCase() : "0x" + v.toString(16).toUpperCase());
export type DiffGroup = { kind: string; old: number; new: number; count: number; sites: number[] };

// one row per (kind, old -> new): a shifted struct field shows as one row with all its instructions
export function groupDiffs(diffs: FieldDiff[]): DiffGroup[] {
    const g = new Map<string, DiffGroup>();
    const seen = new Set<string>();
    for (const d of diffs) {
        if (seen.has(`${d.at}:${d.kind}`)) continue;  // overlapping windows see the same instruction
        seen.add(`${d.at}:${d.kind}`);
        const k = `${d.kind} ${d.old} ${d.new}`;
        const x = g.get(k) ?? { kind: d.kind, old: d.old, new: d.new, count: 0, sites: [] };
        x.count++; x.sites.push(d.at); g.set(k, x);
    }
    return [...g.values()].sort((a, b) => b.count - a.count || a.old - b.old);
}
export const groupLine = (x: DiffGroup) => `${x.kind} ${sx(x.old)} -> ${sx(x.new)}`;

// ---- vtables ----
export type Vt = { va: number; fns: number[] };
const inExec = (pe: Pe, va: number) => pe.exec.some(([a, b]) => va - pe.base >= a && va - pe.base < b);

// a function start by its padding: int3/nop/ret before it, or `ret n`
const fnStart = (pe: Pe, va: number) => { const p = va - pe.base; return [0xcc, 0xc3, 0x90].includes(pe.img[p - 1]) || pe.img[p - 3] === 0xc2; };

export function findVtables(pe: Pe, minLen = 2): Vt[] {
    const outside = (x: number) => !pe.exec.some(([a, b]) => x >= a && x < b);
    // no .reloc (the Steam exe is fixed-base): every aligned dword outside code that points at a function start
    const rs = pe.relocs.size ? [...pe.relocs].filter(outside).sort((a, b) => a - b)
        : Array.from({ length: (pe.img.length - 0x1000) >> 2 }, (_, i) => 0x1000 + 4 * i).filter((x) => outside(x) && inExec(pe, pe.img.readUInt32LE(x)) && fnStart(pe, pe.img.readUInt32LE(x)));
    const out: Vt[] = [];
    let cur: Vt | null = null, prev = -8;
    for (const x of rs) {
        const v = pe.img.readUInt32LE(x);
        if (inExec(pe, v)) {
            if (cur && x === prev + 4) cur.fns.push(v);
            else { if (cur && cur.fns.length >= minLen) out.push(cur); cur = { va: pe.base + x, fns: [v] }; }
            prev = x;
        } else { if (cur && cur.fns.length >= minLen) out.push(cur); cur = null; }
    }
    if (cur && cur.fns.length >= minLen) out.push(cur);
    return out;
}

function sig(pe: Pe, va: number): string | null {
    const rva = va - pe.base;
    if (rva + 24 > pe.img.length) return null;
    const b = Buffer.from(pe.img.subarray(rva, rva + 24));
    for (let i = 0; i < 24; i++) if (pe.mask[rva + i]) b[i] = 0;
    return b.toString("hex") + ":" + Array.from(pe.mask.subarray(rva, rva + 24)).join("");
}

export type VtMap = { ref: number; new: number | null; slots: number; newSlots: number; votes: number; ties: number; shift: number; slotShift: (number | null)[] };

// `known`: function pairs already mapped (address map rows); median delta breaks ties between equal-vote candidates
export function mapVtables(r: Pe, o: Pe, known = new Map<number, number>(), median = 0): VtMap[] {
    const rv = findVtables(r), ov = findVtables(o);
    const fnMap = new Map(known);
    const sigs = (pe: Pe, vts: Vt[]) => {
        const m = new Map<string, Set<number>>();
        for (const v of vts) for (const f of v.fns) { const k = sig(pe, f); if (k) m.set(k, (m.get(k) ?? new Set()).add(f)); }
        return m;
    };
    const rs = sigs(r, rv), os = sigs(o, ov);
    for (const [k, fs] of rs) { const g = os.get(k); if (fs.size === 1 && g?.size === 1 && !fnMap.has([...fs][0])) fnMap.set([...fs][0], [...g][0]); }
    const idx = new Map<number, [number, number][]>();
    ov.forEach((v, vi) => v.fns.forEach((f, j) => idx.set(f, [...(idx.get(f) ?? []), [vi, j]])));
    const rows = rv.map((v) => {
        const votes = new Map<string, number>();
        v.fns.forEach((f, i) => {
            const g = fnMap.get(f), at = g === undefined ? undefined : idx.get(g);
            if (at && at.length <= 8) for (const [vi, j] of at) votes.set(`${vi}:${j - i}`, (votes.get(`${vi}:${j - i}`) ?? 0) + 1);  // shared stubs vote nowhere
        });
        const row: VtMap = { ref: v.va, new: null, slots: v.fns.length, newSlots: 0, votes: 0, ties: 0, shift: 0, slotShift: v.fns.map(() => null) };
        if (!votes.size) return row;
        const top = Math.max(...votes.values());
        const cands = [...votes].filter(([, n]) => n === top).map(([k]) => k.split(":").map(Number));
        cands.sort((a, b) => Number(ov[a[0]].fns.length !== v.fns.length) - Number(ov[b[0]].fns.length !== v.fns.length)
            || Math.abs(ov[a[0]].va - v.va - median) - Math.abs(ov[b[0]].va - v.va - median));
        const [vi, shift] = cands[0], w = ov[vi];
        Object.assign(row, { new: w.va, newSlots: w.fns.length, votes: top, ties: cands.length - 1, shift });
        row.slotShift = v.fns.map((f, i) => {
            const g = fnMap.get(f);
            if (g === undefined) return null;
            let best: number | null = null;
            w.fns.forEach((h, j) => { if (h === g && (best === null || Math.abs(j - i - shift) < Math.abs(best - shift))) best = j - i; });
            return best;
        });
        return row;
    });
    // no votes (only shared or non-unique functions): the nearest mapped vtable's delta, if the other exe has a
    // vtable of the same length there (votes 0 = inferred, like `near` in the address map)
    const byVa = new Map(ov.map((w) => [w.va, w]));
    const mapped = rows.filter((x) => x.new !== null);
    for (const x of rows) {
        if (x.new !== null || !mapped.length) continue;
        const n = mapped.reduce((a, b) => (Math.abs(b.ref - x.ref) < Math.abs(a.ref - x.ref) ? b : a));
        const w = byVa.get(x.ref + n.new! - n.ref);
        if (w && w.fns.length === x.slots) Object.assign(x, { new: w.va, newSlots: w.fns.length, slotShift: x.slotShift.map(() => null) });
    }
    // slots without a mapped function take the shift of their known neighbours when those agree
    for (const x of rows) {
        if (x.new === null) continue;
        const s = x.slotShift, known = s.map((v, i) => (v === null ? -1 : i)).filter((i) => i >= 0);
        if (!known.length) { if (x.slots === x.newSlots) x.slotShift = s.map(() => 0); continue; }
        x.slotShift = s.map((v, i) => {
            if (v !== null) return v;
            const p = known.filter((k) => k < i).pop(), q = known.find((k) => k > i);
            const a = p === undefined ? null : s[p], b = q === undefined ? null : s[q];
            return a === null ? b : b === null || a === b ? a : null;
        });
    }
    return rows;
}

// "0-3 +0, 4-9 +1, 10 ?": slot shift runs
export function shiftRuns(s: (number | null)[]): string {
    const out: string[] = [];
    for (let i = 0; i < s.length;) {
        let j = i; while (j + 1 < s.length && s[j + 1] === s[i]) j++;
        out.push(`${i === j ? i : `${i}-${j}`} ${s[i] === null ? "?" : (s[i]! < 0 ? "" : "+") + s[i]}`);
        i = j + 1;
    }
    return out.join(", ");
}

function selfCheck() {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`exe-port-diff self-check FAILED: ${what}`); process.exit(1); } };
    const pe = (img: Buffer, relocs: number[] = []): Pe => ({ base: 0x400000, img, exec: [[0x1000, 0x2000]], mask: new Uint8Array(img.length), relocs: new Set(relocs) });
    // offset diff: mov eax,[ecx+0x918]; add ecx,4; mov [esi+0x10],1; ret  vs  0x91C, 8, same, ret
    const code = (d: number, i: number) => Buffer.from([0x90, 0x8b, 0x81, d & 0xff, d >> 8, 0, 0, 0x83, 0xc1, i, 0xc7, 0x46, 0x10, 1, 0, 0, 0, 0xc3]);
    const ri = Buffer.alloc(0x4000), oi = Buffer.alloc(0x4000);
    code(0x918, 4).copy(ri, 0x1100); code(0x91c, 8).copy(oi, 0x1180);
    const r = pe(ri), o = pe(oi);
    const diffs = fieldDiffs(r, o, 0x1100, 0x1112, 0x80, 0x1101);
    const groups = groupDiffs([...diffs, ...diffs]);
    ok(groups.length === 2 && groupLine(groups.find((g) => g.kind === "disp")!) === "disp 0x918 -> 0x91C" && groups.every((g) => g.count === 1), "disp + imm groups, duplicates folded");
    ok(groups.find((g) => g.kind === "imm")!.new === 8, "imm 4 -> 8");
    ok(fieldDiffs(r, r, 0x1100, 0x1112, 0, 0x1101).length === 0, "identity: no diffs");
    const fm = fuzzyMask(r, 0x1100, 0x1112, 0x1101);
    ok(fm[3] === 1 && fm[2] === 0 && fm[9] === 1 && fm[13] === 1, "fuzzy mask covers disp and imm only");
    ok(insStarts(ri, 0x1100, 0x1112, 0x1107)[0] === 0x1100, "sync back to the window start");
    // vtables: A (3 slots) and B (2 slots) at 0x3000/0x3010; other: A gets a new slot 1, code moves +0x40
    let seed = 7;
    const rnd = () => (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 16 & 0xff;
    const vr = Buffer.alloc(0x4000), vo = Buffer.alloc(0x4000);
    for (let i = 0x1000; i < 0x1f00; i++) vr[i] = rnd();
    vr.copy(vo, 0x1040, 0x1000, 0x1f00);
    const fA = [0x1100, 0x1200, 0x1300], fB = [0x1400, 0x1500], fNew = 0x1e80;
    for (let i = 0; i < 24; i++) vo[fNew + i] = rnd();
    fA.forEach((f, i) => vr.writeUInt32LE(0x400000 + f, 0x3000 + 4 * i));
    fB.forEach((f, i) => vr.writeUInt32LE(0x400000 + f, 0x3010 + 4 * i));
    [fA[0] + 0x40, fNew, fA[1] + 0x40, fA[2] + 0x40].forEach((f, i) => vo.writeUInt32LE(0x400000 + f, 0x3000 + 4 * i));
    fB.forEach((f, i) => vo.writeUInt32LE(0x400000 + f + 0x40, 0x3020 + 4 * i));
    const rp = pe(vr, [0x3000, 0x3004, 0x3008, 0x3010, 0x3014]), op = pe(vo, [0x3000, 0x3004, 0x3008, 0x300c, 0x3020, 0x3024]);
    ok(findVtables(rp).map((v) => v.fns.length).join() === "3,2", "two vtables found");
    const m = mapVtables(rp, op);
    ok(m[0].new === 0x403000 && shiftRuns(m[0].slotShift) === "0 +0, 1-2 +1", `vtable A slot shift (${shiftRuns(m[0].slotShift)})`);
    ok(m[1].new === 0x403020 && m[1].slotShift.every((s) => s === 0), "vtable B moved, no shift");
    ok(mapVtables(rp, rp).every((x) => x.new === x.ref && x.slotShift.every((s) => s === 0)), "identity");
    console.log("exe-port-diff self-check: synthetic ok");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    if (process.argv.includes("--self-check")) selfCheck();
    else { console.error("usage: node tools/exe-port-diff.ts --self-check (library for tools/exe-addr-map.ts)"); process.exit(2); }
}
