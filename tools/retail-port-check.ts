// Retail (disc / non-Steam) TS3W.exe 1.67 port check (docs/273): every row of a TS3A table checked against the other
// exe's bytes, independent of exe-addr-map's signature search (which put 30+ code rows on look-alike functions here).
//
//   node tools/retail-port-check.ts --other <retail TS3W.exe> [--steam <Steam TS3W.exe>] [--table native/ts3native/ts3addr-retail167.h] [--list] [--csv]
//   node tools/retail-port-check.ts --self-check
//
// TS3W.exe has no .reloc table: every code dword that points into the image counts as a pointer. Anchors: every Steam
// .text position holding such a pointer or an E8/E9 rel32 whose 28-byte window (pointers and rel32 masked) occurs exactly
// once in each exe gives a position pair and a value pair (pointer value or call target = one vote). Evidence for s:
//   vote    >= 2 votes name one target and other targets get < 1/3 of that (code: or 1 clean vote + 16 equal bytes)
//   code    s in .text: the anchors around s (<= 0x2000) move by d and 24 masked bytes at s, s+d agree; or the 48 masked
//           bytes at s occur exactly once in the other .text
//   unused  FAIL / unresolved, but the retail build reads it through no TS3A / TS3A_R (tools/ts3addr-check.ts uses): not counted
//   near    data: the nearest clean voted targets below and above s (<= 0x200) move by the same delta (near1: below only)
// A row passes when its evidence names its own address; FAIL = other address or no evidence; 0 rows = unresolved with
// the suggestion (review, then docs/data/retail167-overrides.csv). Reads exe bytes; prints addresses and counts only.
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { createHash } from "node:crypto";
import { parsePe } from "./exe-addr-map.ts";
import type { Pe } from "./exe-port-diff.ts";
import { uses } from "./ts3addr-check.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const W0 = 12, W1 = 16;  // window around an anchor position

type Anchors = { pos: Map<number, number>; posSorted: number[]; votes: Map<number, Map<number, number>>; shifts: [number, number, number, number][] };

const key = (pe: Pe, p: number) => {
    const b = Buffer.from(pe.img.subarray(p - W0, p + W1));
    for (let i = 0; i < b.length; i++) if (pe.mask[p - W0 + i]) b[i] = 0;
    return createHash("sha1").update(b).update(Buffer.from(pe.mask.subarray(p - W0, p + W1))).digest("base64");
};
// positions whose 4 bytes are a relocated pointer or a code rel32 (mask starts there)
function sites(pe: Pe): number[] {
    const out: number[] = [];
    for (const [a, b] of pe.exec) for (let i = a + W0; i + W1 <= b; i++) if (pe.mask[i] && !pe.mask[i - 1]) out.push(i);
    return out;
}
function uniq(pe: Pe): Map<string, number> {
    const m = new Map<string, number>();
    for (const p of sites(pe)) { const k = key(pe, p); m.set(k, m.has(k) ? -1 : p); }
    return m;
}
// TS3W.exe ships without a .reloc table (fixed base): treat every code dword that points into the image as a pointer
// (masked, voted). shortcut: a false pointer only adds a conflicting vote, which the checks treat as no evidence.
export function addPointers(pe: Pe) {
    if (pe.relocs.size) return;
    const lo = pe.base + 0x1000, hi = pe.base + pe.img.length;
    for (const [a, b] of pe.exec) for (let i = a; i + 4 <= b; i++) {
        if (pe.mask[i] && !pe.relocs.has(i - 1) && !pe.relocs.has(i - 2) && !pe.relocs.has(i - 3)) continue;  // inside a rel32
        const v = pe.img.readUInt32LE(i);
        if (v >= lo && v < hi) { pe.relocs.add(i); pe.mask.fill(1, i, i + 4); }  // no skip: "cc cc a1 00" may look like a pointer just before the real one
    }
}
const valueAt = (pe: Pe, p: number) => pe.relocs.has(p) ? pe.img.readUInt32LE(p) : pe.base + p + 4 + pe.img.readInt32LE(p);

// secs: Steam VA ranges of the data sections. A section whose moved clean votes (>= 2 votes, no conflict, delta != 0;
// delta 0 = an immediate that only looks like an address) all share one delta is shifted as a whole (.data here: +0x1000).
export function anchors(r: Pe, o: Pe, secs: [number, number][] = []): Anchors {
    const ur = uniq(r), uo = uniq(o), pos = new Map<number, number>(), votes = new Map<number, Map<number, number>>();
    for (const [k, p] of ur) {
        const q = uo.get(k);
        if (p < 0 || q === undefined || q < 0) continue;
        pos.set(p, q);
        if (r.relocs.has(p) !== o.relocs.has(q)) continue;
        const s = valueAt(r, p), t = valueAt(o, q);
        const v = votes.get(s) ?? new Map<number, number>();
        v.set(t, (v.get(t) ?? 0) + 1); votes.set(s, v);
    }
    const shifts: [number, number, number, number][] = [];
    for (const [lo, hi] of secs) {
        const d = new Map<number, number>();
        for (const [t, v] of votes) { if (t < lo || t >= hi || v.size !== 1) continue; const [[u, n]] = [...v]; if (n >= 2 && u !== t) d.set(u - t, (d.get(u - t) ?? 0) + 1); }
        if (d.size === 1) { const [[dd, n]] = [...d]; if (n >= 100) shifts.push([lo, hi, dd, n]); }
    }
    return { pos, posSorted: [...pos.keys()].sort((a, b) => a - b), votes, shifts };
}

const inText = (pe: Pe, va: number) => pe.exec.some(([a, b]) => va - pe.base >= a && va - pe.base < b);
function bisect(a: number[], x: number) { let lo = 0, hi = a.length; while (lo < hi) { const m = (lo + hi) >> 1; if (a[m] < x) lo = m + 1; else hi = m; } return lo; }
// delta of the nearest anchors before and after rva (both within lim and equal), else null
function anchorDelta(an: Anchors, rva: number, lim = 0x800): number | null {
    const i = bisect(an.posSorted, rva), a = an.posSorted[i - 1], b = an.posSorted[i];
    if (a === undefined || b === undefined || rva - a > lim || b - rva > lim) return null;
    const da = an.pos.get(a)! - a, db = an.pos.get(b)! - b;
    return da === db ? da : null;
}
// masked equal bytes from s (Steam rva) vs t (other rva), up to cap
const run = (r: Pe, o: Pe, s: number, t: number, cap = 256) => {
    let i = 0;
    for (; i < cap && t + i < o.img.length; i++) if (!r.mask[s + i] && !o.mask[t + i] && r.img[s + i] !== o.img[t + i]) break;
    return i;
};
// other-exe positions whose masked bytes equal Steam's [s, s+n)
function sigHits(r: Pe, o: Pe, s: number, n: number): number[] {
    let bs = s, bl = 0;
    for (let i = s; i < s + n;) { if (r.mask[i]) { i++; continue; } let j = i; while (j < s + n && !r.mask[j]) j++; if (j - i > bl) { bs = i; bl = j - i; } i = j; }
    if (bl < 4) return [];
    const needle = r.img.subarray(bs, bs + bl), out: number[] = [];
    for (const [a, b] of o.exec) {
        const hay = o.img.subarray(a, b);
        for (let p = hay.indexOf(needle); p >= 0 && out.length < 4; p = hay.indexOf(needle, p + 1)) { const t = a + p - (bs - s); if (run(r, o, s, t, n) >= n) out.push(t); }
    }
    return out;
}
function vote1(an: Anchors, s: number): { r: number; n: number; conflicts: number } | null {
    const v = an.votes.get(s); if (!v) return null;
    const best = [...v].sort((x, y) => y[1] - x[1])[0];
    return { r: best[0], n: best[1], conflicts: [...v].filter(([t]) => t !== best[0]).reduce((x, [, c]) => x + c, 0) };
}
// data: the nearest clean voted targets below and above s (<= lim away) move by the same delta
function nearDelta(an: Anchors, voted: number[], s: number, lim = 0x200): { d: number; two: boolean } | null {
    const i = bisect(voted, s + 1) - 1, clean = (t: number) => { const v = vote1(an, t); return v && v.conflicts === 0 && v.n >= 2 ? v.r - t : null; };
    let lo: number | null = null, hi: number | null = null;
    for (let j = i; j >= 0 && s - voted[j] <= lim && lo === null; j--) lo = clean(voted[j]);
    for (let j = i + 1; j < voted.length && voted[j] - s <= lim && hi === null; j++) hi = clean(voted[j]);
    if (lo !== null && hi !== null) return lo === hi ? { d: lo, two: true } : null;
    return lo !== null ? { d: lo, two: false } : null;
}

// a C string (>= 6 printable chars) at rva, else null
const cstr = (pe: Pe, rva: number) => {
    let e = rva; while (e < pe.img.length && e - rva < 256 && pe.img[e] >= 0x20 && pe.img[e] < 0x7f) e++;
    return e - rva >= 6 && pe.img[e] === 0 ? pe.img.subarray(rva, e + 1) : null;
};
const dataStart = (pe: Pe) => Math.max(...pe.exec.map(([, b]) => b));
// where pointer value P (Steam) lands in the other exe: code by judge, data by vote, strings by a unique equal string
function mapPtr(rPe: Pe, oPe: Pe, an: Anchors, voted: number[], p: number): number | null {
    if (p < rPe.base + 0x1000 || p >= rPe.base + rPe.img.length) return null;
    if (inText(rPe, p)) { const j = judge(rPe, oPe, an, voted, p, false); return j ? j.r : null; }
    const v = vote1(an, p);
    if (v && v.n >= 2 && v.n >= 3 * v.conflicts) return v.r;
    const st = cstr(rPe, p - rPe.base);
    if (!st) return null;
    const ds = dataStart(oPe), hay = oPe.img.subarray(ds), i = hay.indexOf(st);
    return i >= 0 && hay.indexOf(st, i + 1) < 0 ? oPe.base + ds + i : null;
}
// data: the pointers stored at and around s (s-8 .. s+8) map to pointers stored at exactly one place in the other
// exe's data, at the same spacing (table rows like the IC {fn, name} pairs, vtable slots)
function content(rPe: Pe, oPe: Pe, an: Anchors, voted: number[], s: number): { r: number; kind: string; note: string } | null {
    const rva = s - rPe.base, mapped: [number, number][] = [];
    for (const k of [0, 4, -4, 8, -8]) {
        if (rva + k < 0 || rva + k + 4 > rPe.img.length) continue;
        const p = rPe.img.readUInt32LE(rva + k), q = p ? mapPtr(rPe, oPe, an, voted, p) : null;
        if (q !== null) mapped.push([k, q]);
    }
    if (!mapped.length) return null;
    const [k0, q0] = mapped[0], ds = dataStart(oPe), cands: number[] = [];
    for (let i = ds; i + 4 <= oPe.img.length && cands.length < 8; i += 4) {
        if (oPe.img.readUInt32LE(i) !== q0) continue;
        const r = oPe.base + i - k0;
        if (mapped.every(([k, q]) => r - oPe.base + k + 4 <= oPe.img.length && oPe.img.readUInt32LE(r - oPe.base + k) === q)) cands.push(r);
    }
    if (cands.length !== 1 || (mapped.length < 2 && s % 4)) return null;
    return { r: cands[0], kind: "content", note: `${mapped.length} stored pointer(s) map there, unique` };
}

// code: the data dwords holding s (vtable slots, function tables) mapped to the other exe hold one value there.
// This names the copy the game really calls through when identical small functions were folded differently.
const slotIndex = new WeakMap<Pe, Map<number, number[]>>();
function slots(rPe: Pe, oPe: Pe, an: Anchors, voted: number[], s: number): { r: number; kind: string; note: string } | null {
    let idx = slotIndex.get(rPe);
    if (!idx) {
        idx = new Map();
        for (let i = dataStart(rPe); i + 4 <= rPe.img.length; i += 4) { const v = rPe.img.readUInt32LE(i); if (inText(rPe, v)) { const l = idx.get(v); if (l) l.push(i); else idx.set(v, [i]); } }
        slotIndex.set(rPe, idx);
    }
    const tally = new Map<number, number>();
    for (const i of idx.get(s) ?? []) {
        const j = judge(rPe, oPe, an, voted, rPe.base + i);  // data path: never back into slots()
        if (!j) continue;
        const v = oPe.img.readUInt32LE(j.r - oPe.base);
        tally.set(v, (tally.get(v) ?? 0) + 1);
    }
    const [best, ...rest] = [...tally].filter(([v]) => inText(oPe, v)).sort((a, b) => b[1] - a[1]);
    if (!best || best[1] < 3 * rest.reduce((x, [, n]) => x + n, 0) || (rest.length && best[1] < 3)) return null;
    return { r: best[0], kind: "slots", note: `${best[1]} data slot(s) hold it${rest.length ? `, ${rest.length} other value(s)` : ""}` };
}

// best Steam -> other candidate for s with its evidence, or null
export function judge(rPe: Pe, oPe: Pe, an: Anchors, voted: number[], s: number, deep = true): { r: number; kind: string; note: string } | null {
    const v = vote1(an, s), rva = s - rPe.base;
    if (v && v.n >= 2 && v.n >= 3 * v.conflicts) return { r: v.r, kind: "vote", note: `${v.n}/${v.conflicts} votes` };
    if (inText(rPe, s)) {
        const sl = deep ? slots(rPe, oPe, an, voted, s) : null;
        if (sl) return sl;
        const d = anchorDelta(an, rva, 0x2000);
        if (d !== null && run(rPe, oPe, rva, rva + d) >= 24) return { r: s + d, kind: "code", note: "anchors + 24 bytes" };
        const hits = sigHits(rPe, oPe, rva, 48);
        if (hits.length === 1) return { r: hits[0] + oPe.base, kind: "code", note: "unique 48-byte signature" };
        if (v && v.conflicts === 0 && run(rPe, oPe, rva, v.r - oPe.base) >= 16) return { r: v.r, kind: "vote", note: "1 vote + 16 bytes" };
        return null;
    }
    const st = rPe.img[rva - 1] === 0 ? cstr(rPe, rva) : null;
    if (st) {  // s is a string: the same string once in the other exe's data
        const ds = dataStart(oPe), hay = oPe.img.subarray(ds), hits: number[] = [];
        for (let i = hay.indexOf(st); i >= 0 && hits.length < 2; i = hay.indexOf(st, i + 1)) if (hay[i - 1] === 0) hits.push(i);
        if (hits.length === 1) return { r: oPe.base + ds + hits[0], kind: "string", note: `same ${st.length - 1}-char string, unique` };
    }
    const c = deep ? content(rPe, oPe, an, voted, s) : null;
    if (c) return c;
    if (v && v.conflicts === 0) return { r: v.r, kind: "vote", note: "1 vote" };
    const sh = an.shifts.find(([lo, hi]) => s >= lo && s < hi);
    if (sh) return { r: s + sh[2], kind: "shift", note: `section moved as a whole by ${sh[2].toString(16)} (${sh[3]} clean votes, no other delta)` };
    const nd = nearDelta(an, voted, s);
    if (nd) return { r: s + nd.d, kind: nd.two ? "near" : "near1", note: nd.two ? "voted neighbours both sides" : "voted neighbour below" };
    return null;
}

// code row with no other evidence: the masked bytes equal around s and r (back + forward >= 48) occur only at r
function region(rPe: Pe, oPe: Pe, s: number, r: number): string | null {
    if (!inText(rPe, s) || !inText(oPe, r)) return null;
    const a = s - rPe.base, b = r - oPe.base;
    let back = 0; while (back < 256 && (rPe.mask[a - 1 - back] || oPe.mask[b - 1 - back] || rPe.img[a - 1 - back] === oPe.img[b - 1 - back])) back++;
    const fwd = run(rPe, oPe, a, b);
    if (back + fwd < 48) return null;
    const hits = sigHits(rPe, oPe, a - back, Math.min(back + fwd, 256));
    return hits.length === 1 && hits[0] === b - back ? `${back}+${fwd} equal bytes around it, unique` : null;
}
export type Verdict = { s: number; r: number; kind: string; note: string };
export function check(rPe: Pe, oPe: Pe, an: Anchors, pairs: [number, number][]): Verdict[] {
    const voted = [...an.votes.keys()].sort((a, b) => a - b), out: Verdict[] = [];
    for (const [s, r] of pairs) {
        const j = judge(rPe, oPe, an, voted, s);
        if (r === 0) out.push({ s, r, kind: "unresolved", note: j ? `suggest ${hx(j.r)} ${j.kind} (${j.note})` : "no evidence" });
        else if (!j) { const g = region(rPe, oPe, s, r); out.push(g ? { s, r, kind: "region", note: g } : { s, r, kind: "FAIL", note: "no evidence" }); }
        else if (j.r !== r) out.push({ s, r, kind: "FAIL", note: `evidence says ${hx(j.r)} ${j.kind} (${j.note})` });
        else out.push({ s, r, kind: j.kind, note: j.note });
    }
    return out;
}
const hx = (n: number) => "0x" + (n >>> 0).toString(16).toUpperCase().padStart(8, "0");

// Per-build constants (TS3A_PICK third argument): FNV-1a code hashes over [TS3A(lo), TS3A(hi)) or n bytes at TS3A(at), and
// the operand bytes of monojit's pure-icall copies. Each Steam range must be the same code in retail (masked bytes equal,
// same length) with every moved operand (pointer / rel32 target) mapping as the table or the evidence says; the retail value
// must be the one in the source file.
// cut: a Steam sub-range the other build links elsewhere (the rest must follow on without a gap there)
// bytesOnly: no hash in the source, only the same-code check (monojit's pure-icall copies; their per-build operand bytes are
// TS3A_PICKs and JitPureIcallsOk compares them with the live code at install)
type Const = { name: string; file: string; ranges: [number, number][]; n?: number; patch?: [number, number[]]; cut?: [number, number]; bytesOnly?: boolean };
const CONSTS: Const[] = [
    { name: "FP_FN_FNV", file: "footprint.cpp", ranges: [[0x6663C0, 0x666740]] },
    { name: "SC_CODE_FNV", file: "savecompress.cpp", ranges: [[0x4EB340, 0x4EB3B0], [0x4EB750, 0x4EBFD0], [0x4EC0A0, 0x4EC282]] },
    { name: "WJ_CODE_FNV", file: "waterjps.cpp", ranges: [[0x61F750, 0x61FF93]], cut: [0x61F830, 0x61F890] },  // heap helper, as on EA
    { name: "WJ_NEAR_FNV", file: "waterjps.cpp", ranges: [[0x66DC50, 0x66DC50 + 0x60]], n: 0x60 },
    { name: "kFastCopyFnv", file: "monojit.cpp", ranges: [[0xE7A070, 0xE7A070 + 0x281]], n: 0x281 },
    { name: "MC call", file: "mintcall.cpp", ranges: [[0xE5493E, 0xE5493E + 0xAD]], n: 0xAD },
    { name: "MC callvirt", file: "mintcall.cpp", ranges: [[0xE549EB, 0xE549EB + 0x160]], n: 0x160 },
    { name: "MC push", file: "mintcall.cpp", ranges: [[0xE56287, 0xE56287 + 0x1C2]], n: 0x1C2 },
    { name: "MC ret", file: "mintcall.cpp", ranges: [[0xE591FF, 0xE591FF + 0x60]], n: 0x60 },
    { name: "MC ret MonoPatcher", file: "mintcall.cpp", ranges: [[0xE591FF, 0xE591FF + 0x60]], n: 0x60, patch: [0x19, [0x90, 0xE9]] },
    { name: "MC head", file: "mintcall.cpp", ranges: [[0xE543C7, 0xE543C7 + 0x127]], n: 0x127 },
    { name: "kPureGetLength", file: "monojit.cpp", ranges: [[0xE79FC0, 0xE79FC0 + 0x3A]], n: 0x3A, bytesOnly: true },
    { name: "kPureGetLower", file: "monojit.cpp", ranges: [[0xE7A000, 0xE7A000 + 0x3D]], n: 0x3D, bytesOnly: true },
    { name: "kPureGetRank", file: "monojit.cpp", ranges: [[0xE79FB0, 0xE79FB0 + 0xD]], n: 0xD, bytesOnly: true },
];
// Steam addresses the retail build translates outside the table (ini values): [steam, retail, where]
const EXTRA: [number, number, string][] = [[0x76B470, 0x76B530, "[ThreadStack] Skip (threadstack.cpp TsParseSkip)"]];
const fnv = (h: number, b: Uint8Array) => { for (const x of b) h = Math.imul(h ^ x, 0x01000193) >>> 0; return h; };
function consts(rPe: Pe, oPe: Pe, an: Anchors, tr: Map<number, number>): number {
    const voted = [...an.votes.keys()].sort((a, b) => a - b), map = (a: number) => tr.get(a) ?? judge(rPe, oPe, an, voted, a)?.r;
    let bad = 0;
    for (const c of CONSTS) {
        let h = 0x811C9DC5, hs = 0x811C9DC5, ops = 0, opsOk = 0, same = true;
        for (const [lo, hi] of c.ranges) {
            const rlo0 = tr.get(lo), rhi = c.n ? (rlo0 ?? 0) + c.n : tr.get(hi);
            const pieces: [number, number][] = c.cut ? [[lo, c.cut[0]], [c.cut[1], hi]] : [[lo, hi]];
            if (!rlo0 || !rhi || rhi - rlo0 !== pieces.reduce((x, [p, q]) => x + q - p, 0)) { same = false; continue; }
            const whole = (pe: Pe, x: number, y: number) => { const w = Buffer.from(pe.img.subarray(x - pe.base, y - pe.base)); if (c.patch) w.set(c.patch[1], c.patch[0]); return w; };
            h = fnv(h, whole(oPe, rlo0, rhi)), hs = fnv(hs, whole(rPe, lo, hi));
            let rlo = rlo0;
            for (const [plo, phi] of pieces) {
            const a = plo - rPe.base, b = rlo - oPe.base, len = phi - plo, lo = plo;
            const sb = whole(rPe, plo, phi), ob = whole(oPe, rlo, rlo + len);
            const rb = rlo; rlo += len;
            for (let i = 0; i < len; i++) {
                if (!rPe.mask[a + i]) { if (sb[i] !== ob[i]) same = false; continue; }
                if (rPe.mask[a + i - 1] && i) continue;
                ops++;  // operand start: a pointer, or a rel32 (value = target)
                const p = rPe.relocs.has(a + i) ? rPe.img.readUInt32LE(a + i) : lo + i + 4 + rPe.img.readInt32LE(a + i);
                const q = oPe.relocs.has(b + i) ? oPe.img.readUInt32LE(b + i) : rb + i + 4 + oPe.img.readInt32LE(b + i);
                // equal bytes: nothing moved (or a byte run that only looks like a pointer); an E8 byte inside the operand
                // (mov [ebp-18h], eax; call) may hide the real call one byte later: its target must map then
                const p1 = lo + i + 5 + rPe.img.readInt32LE(a + i + 1), q1 = rb + i + 5 + oPe.img.readInt32LE(b + i + 1);
                if (p === q || map(p) === q || rPe.img[a + i] === 0xE8 && map(p1) === q1) opsOk++;
                else if (process.env.RPC_DEBUG) console.log(`    operand ${hx(lo + i)}: ${hx(p)} -> ${hx(q)}, map says ${hx(map(p) ?? 0)}`);
                i += 3;
            }
            }
        }
        const src = readFileSync(join(repo, "native/ts3native", c.file), "utf8"), want = "0x" + h.toString(16).toUpperCase().padStart(8, "0");
        const inSrc = new RegExp(`TS3A_PICK\\(${hx(hs)}, 0x[0-9A-F]{8}, ${want}\\)`).test(src);
        const ok = same && ops === opsOk && (inSrc || !!c.bytesOnly);
        if (!ok) bad++;
        console.log(`  ${ok ? "ok  " : "FAIL"} ${c.name} (${c.file}): steam ${hx(hs)} retail ${want}, same code ${same ? "yes" : "NO"}, operands ${opsOk}/${ops} map, in source ${inSrc ? "yes" : "NO"}`);
    }
    for (const [st, rt, where] of EXTRA) {
        const j = judge(rPe, oPe, an, voted, st), ok = j?.r === rt;
        if (!ok) bad++;
        console.log(`  ${ok ? "ok  " : "FAIL"} ${hx(st)} -> ${hx(rt)} ${where}: evidence ${j ? hx(j.r) + " " + j.kind : "none"}`);
    }
    return bad;
}

// VA ranges of the non-executable sections
export function dataSecs(buf: Buffer): [number, number][] {
    const pe = buf.readUInt32LE(0x3c), n = buf.readUInt16LE(pe + 6), opt = pe + 24, base = buf.readUInt32LE(opt + 28), out: [number, number][] = [];
    for (let i = 0, h = opt + buf.readUInt16LE(pe + 20); i < n; i++, h += 40)
        if (!(buf.readUInt32LE(h + 36) & 0x20000000)) out.push([base + buf.readUInt32LE(h + 12), base + buf.readUInt32LE(h + 12) + Math.max(buf.readUInt32LE(h + 8), buf.readUInt32LE(h + 16))]);
    return out;
}
export function readTable(text: string): { pairs: [number, number][]; pe: number; size: number } {
    const pairs = [...text.matchAll(/{0x([0-9A-F]+), 0x([0-9A-F]+)}/g)].map((m) => [parseInt(m[1], 16), parseInt(m[2], 16)] as [number, number]);
    const pe = parseInt(/TS3A_HOST_PE 0x([0-9A-F]+)/.exec(text)?.[1] ?? "0", 16), size = parseInt(/TS3A_HOST_SIZE 0x([0-9A-F]+)/.exec(text)?.[1] ?? "0", 16);
    return { pairs, pe, size };
}

function selfCheck() {
    let fails = 0;
    const ok = (c: boolean, m: string) => { if (!c) { fails++; console.log("FAIL " + m); } };
    // two tiny images: same code, the second shifted by 0x10, one pointer to data moved by 0x1000
    const mk = (shift: number, data: number) => {
        const img = Buffer.alloc(0x3000), mask = new Uint8Array(0x3000);
        for (let i = 0x1000; i < 0x1800; i++) img[i + shift] = (Math.imul(i, 2654435761) >>> 13) & 0xff;  // no repeats: windows unique
        for (const p of [0x1100, 0x1300, 0x1500]) { img.writeUInt32LE(0x400000 + data, p + shift); mask.fill(1, p + shift, p + shift + 4); }
        return { base: 0x400000, img, exec: [[0x1000, 0x2000]] as [number, number][], mask, relocs: new Set([0x1100 + shift, 0x1300 + shift, 0x1500 + shift]) } as unknown as Pe;
    };
    const a = mk(0, 0x2800), b = mk(0x10, 0x2900), an = anchors(a, b);
    ok(an.pos.get(0x1100) === 0x1110, "anchor position pair");
    ok(vote1(an, 0x402800)?.r === 0x402900 && vote1(an, 0x402800)?.n === 3, "pointer vote");
    const v = check(a, b, an, [[0x402800, 0x402900], [0x402810, 0x402910], [0x401200, 0x401210], [0x401200, 0x401220], [0x402800, 0x402800], [0x401234, 0]]);
    ok(v.map((x) => x.kind).join() === "vote,near1,code,FAIL,FAIL,unresolved", "verdicts " + v.map((x) => x.kind).join());
    ok(v[5].note.includes("0x00401244"), "unresolved row gets a suggestion: " + v[5].note);
    ok(readTable("#define TS3A_HOST_PE 0x52D872DA\n    {0x00401000, 0x00401002},\n").pairs[0][1] === 0x401002, "read table");
    console.log(fails ? `retail-port-check self-check: ${fails} FAIL` : "retail-port-check self-check: ok");
    process.exit(fails ? 1 : 0);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { steam: { type: "string" }, other: { type: "string" }, table: { type: "string" }, list: { type: "boolean" }, csv: { type: "boolean" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck();
    if (!v.other) { console.error("usage: node tools/retail-port-check.ts --other <retail TS3W.exe> [--steam <exe>] [--table <hdr>] [--list] | --self-check"); process.exit(2); }
    const rBuf = readFileSync(v.steam ?? join(repo, "game-copy/bin/TS3W.exe")), oBuf = readFileSync(v.other);
    const rPe = parsePe(rBuf), oPe = parsePe(oBuf);
    addPointers(rPe); addPointers(oPe);
    const an = anchors(rPe, oPe, dataSecs(rBuf));
    for (const [lo, hi, d, n] of an.shifts) console.log(`section ${hx(lo)}..${hx(hi)} shifted by ${d.toString(16)} (${n} clean votes)`);
    const tableText = readFileSync(join(repo, v.table ?? "native/ts3native/ts3addr-retail167.h"), "utf8"), t = readTable(tableText);
    const nt = oBuf.readUInt32LE(0x3c), peTs = oBuf.readUInt32LE(nt + 8), soi = oBuf.readUInt32LE(nt + 24 + 56);
    const hostOk = peTs === t.pe && soi === t.size;
    const res = check(rPe, oPe, an, t.pairs), by: Record<string, number> = {};
    // hand-verified rows (evidence written by a person, not by this tool's --csv) and unresolved probe-only rows
    const manual = new Map([...readFileSync(join(repo, "docs/data/retail167-overrides.csv"), "utf8").matchAll(/^(0x[0-9A-F]+),(0x[0-9A-F]+),[^\n]*?,"(?!retail-port-check)/gm)].map((m) => [parseInt(m[1], 16), parseInt(m[2], 16)]));
    const probeOnly = new Set([...tableText.matchAll(/\/\/ unresolved: (0x[0-9A-F]+) \S+ native\/ts3native\/vcallsites\d*\.h/g)].map((m) => parseInt(m[1], 16)));
    const used = uses(repo, true);
    for (const x of res) {
        if (x.kind === "FAIL" && manual.get(x.s) === x.r) x.kind = "manual", x.note = "docs/data/retail167-overrides.csv";
        if (x.kind === "unresolved" && probeOnly.has(x.s)) x.kind = "probe-only", x.note = "vcallsites*.h (probe builds only)";
        // a raw literal port-run also mapped (asm literal, range bound, #ifndef TS3A_TABLE list) that no TS3A / TS3A_R reads here
        if ((x.kind === "unresolved" || x.kind === "FAIL") && !used.has(x.s)) x.kind = "unused", x.note = "no TS3A / TS3A_R use in the retail build: " + x.note;
        by[x.kind] = (by[x.kind] ?? 0) + 1;
    }
    console.log(`anchors ${an.pos.size}, voted targets ${an.votes.size}; host ${hostOk ? "ok" : "MISMATCH"} (pe ${hx(peTs)} size ${hx(soi)})`);
    console.log(`rows ${res.length}: ` + Object.entries(by).map(([k, n]) => `${k} ${n}`).join(", "));
    for (const x of res) if (v.list || x.kind === "FAIL" || x.kind === "unresolved" || x.kind === "manual" || x.kind === "unused") console.log(`  ${x.kind} ${hx(x.s)} -> ${hx(x.r)} ${x.note}`);
    // --csv: override rows (docs/data/retail167-overrides.csv format) for every FAIL / unresolved row that has evidence
    if (v.csv) for (const x of res) { const m = /(?:evidence says|suggest) (0x[0-9A-F]+) (\w+) \((.*)\)$/.exec(x.note); if (m && (x.kind === "FAIL" || x.kind === "unresolved")) console.log(`${hx(x.s)},${m[1]},,"retail-port-check ${m[2]}: ${m[3]}"`); }
    const tr = new Map(t.pairs), cbad = consts(rPe, oPe, an, tr);
    console.log(`table: ${res.length - (by.FAIL ?? 0) - (by.unresolved ?? 0)}/${res.length} rows verified, ${by.FAIL ?? 0} FAIL, ${by.unresolved ?? 0} unresolved; per-build constants ${cbad ? cbad + " FAIL" : "ok"}`);
    process.exit(hostOk && !by.FAIL && !by.unresolved && !cbad ? 0 : 1);
}
