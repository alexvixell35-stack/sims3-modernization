// Map TS3W.exe addresses from the Steam 1.67 exe (all our addresses) to another exe build (e.g. EA app / Origin 1.69),
// so porting a fix starts from an address map instead of re-finding every site by hand.
//
//   node tools/exe-addr-map.ts --other <exe> [--ref <Steam TS3W.exe>] [--addrs file] [--out file.csv|.json] [--all]
//   node tools/exe-addr-map.ts --self-check
//
// --ref     default game-copy/bin/TS3W.exe (the Steam 1.67 exe)
// --addrs   one hex VA per line, optional label after it, '#' comments; default = every code address our native
//           sources name (tools/hook-sites.ts scan)
// --out     default out/exe-addr-map/<other exe name>.csv (gitignored: the map is derived from EA bytes, never commit it)
// --all     print every row (default: summary + problem rows only)
//
// Per address: a byte window around it in the Steam exe, with relocated dwords (.reloc) and call/jmp rel32 operands
// as wildcards, is searched in the other exe's code sections; the window grows (both sides, then one side) until one
// match is left or a cap. Kinds: exact (same bytes), masked (unique only with wildcards), near (ambiguous, one
// candidate has the delta of the nearest unique neighbour), ambiguous N, missing (hint = neighbour delta).
// Data and code pointers read by matched code (relocated dwords, call targets) are mapped too: rows of kind ref
// with votes; an --addrs address outside code is answered from them.
// When no window matches, a fuzzy pass also wildcards displacements, immediates and branch operands (decoded,
// tools/exe-port-diff.ts); every mapped site's window is then diffed field by field: kind offset-changed = a
// displacement or immediate differs (`[reg+0x918]` -> `[reg+0x91C]`), grouped in <out>.offsets.csv. Vtables found in
// both exes are mapped by vote with their slot shifts in <out>.vtables.csv; the ones our sources name are reported.
// shortcut: funcOff walks back to the previous 16-aligned start after int3/ret padding; approximate, a hint only.
// shortcut: no string/IAT-name anchors; add them if a real port leaves many sites missing.
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { scan } from "./hook-sites.ts";
import { fuzzyMask, fieldDiffs, groupDiffs, groupLine, mapVtables, shiftRuns, type Pe, type FieldDiff } from "./exe-port-diff.ts";
import { scanText, sourceFiles } from "./addr-codemod.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const CAP = 256;       // max bytes on each side of the address
const MAXCAND = 64;    // candidates kept for the neighbour tie-break

export type Site = { va: number; label: string; where: string };
export type Row = { ref: number; new: number | null; kind: string; count: number; lo: number; hi: number; cands: number[]; funcOff: string; label: string; where: string; hint?: number; diffs?: FieldDiff[] };

export function parsePe(buf: Buffer): Pe {
    const pe = buf.readUInt32LE(0x3c);
    if (buf.readUInt32LE(pe) !== 0x4550) throw new Error("not a PE file");
    const nsec = buf.readUInt16LE(pe + 6), opt = pe + 24, optSize = buf.readUInt16LE(pe + 20);
    if (buf.readUInt16LE(opt) !== 0x10b) throw new Error("not a 32-bit PE");
    const base = buf.readUInt32LE(opt + 28), size = buf.readUInt32LE(opt + 56);
    const img = Buffer.alloc(size);
    buf.copy(img, 0, 0, Math.min(buf.readUInt32LE(opt + 60), buf.length));
    const exec: [number, number][] = [];
    for (let i = 0, s = opt + optSize; i < nsec; i++, s += 40) {
        const vsz = buf.readUInt32LE(s + 8), rva = buf.readUInt32LE(s + 12), raw = buf.readUInt32LE(s + 16), ptr = buf.readUInt32LE(s + 20);
        buf.copy(img, rva, ptr, ptr + Math.min(raw, vsz || raw));
        if (buf.readUInt32LE(s + 36) & 0x20000000) exec.push([rva, rva + Math.max(vsz, raw)]);
    }
    const relocs: number[] = [];
    const rdir = buf.readUInt32LE(opt + 96 + 5 * 8), rsize = buf.readUInt32LE(opt + 96 + 5 * 8 + 4);
    for (let p = rdir; p < rdir + rsize;) {
        const page = img.readUInt32LE(p), bsz = img.readUInt32LE(p + 4);
        if (bsz < 8) break;
        for (let e = p + 8; e < p + bsz; e += 2) { const v = img.readUInt16LE(e); if (v >> 12 === 3) relocs.push(page + (v & 0xfff)); }
        p += bsz;
    }
    const mask = new Uint8Array(size);
    for (const r of relocs) mask.fill(1, r, r + 4);
    const inExec = (rva: number) => exec.some(([a, b]) => rva >= a && rva < b);
    // rel32 of call/jmp (E8/E9) whose target lands in code: changes whenever code moves
    for (const [a, b] of exec) for (let i = a; i + 5 <= b; i++) {
        if ((img[i] === 0xe8 || img[i] === 0xe9) && !mask[i] && inExec(i + 5 + img.readInt32LE(i + 1))) mask.fill(1, i + 1, i + 5);
    }
    return { base, img, exec, mask, relocs: new Set(relocs) };
}

const sectionOf = (pe: Pe, rva: number) => pe.exec.find(([a, b]) => rva >= a && rva < b);

// candidate window starts in `o` for ref window [lo, hi)
// m = wildcard mask indexed from mb (r.mask, or a fuzzy mask over one site's region)
function search(r: Pe, o: Pe, lo: number, hi: number, m: Uint8Array = r.mask, mb = 0): number[] {
    let bs = lo, bl = 0;  // longest concrete run = indexOf anchor
    for (let i = lo; i < hi;) { if (m[i - mb]) { i++; continue; } let j = i; while (j < hi && !m[j - mb]) j++; if (j - i > bl) { bs = i; bl = j - i; } i = j; }
    if (bl === 0) return [];
    const needle = r.img.subarray(bs, bs + bl), out: number[] = [];
    for (const [a, b] of o.exec) {
        const hay = o.img.subarray(a, b);
        for (let p = hay.indexOf(needle); p >= 0; p = hay.indexOf(needle, p + 1)) {
            const start = a + p - (bs - lo);
            if (start >= a && start + (hi - lo) <= b && verify(r, o, lo, hi, start, m, mb)) out.push(start);
        }
    }
    return out;
}
const verify = (r: Pe, o: Pe, lo: number, hi: number, start: number, m: Uint8Array = r.mask, mb = 0) => {
    for (let i = lo; i < hi; i++) if (!m[i - mb] &&r.img[i] !== o.img[start + i - lo]) return false;
    return true;
};

export function funcOff(pe: Pe, rva: number): string {
    const sec = sectionOf(pe, rva)!;
    for (let p = rva & ~15; p > sec[0] && rva - p < 0x4000; p -= 16) {
        const b1 = pe.img[p - 1], b2 = pe.img[p - 2];
        if ((b1 === 0xcc && (b2 === 0xcc || b2 === 0xc3)) || b1 === 0xc3) return "+0x" + (rva - p).toString(16);
    }
    return "?";
}

function mapSite(r: Pe, o: Pe, s: Site): Row {
    const rva = s.va - r.base, sec = sectionOf(r, rva);
    const row: Row = { ref: s.va, new: null, kind: "missing", count: 0, lo: 0, hi: 0, cands: [], funcOff: "?", label: s.label, where: s.where };
    if (!sec) { row.kind = "not-code"; return row; }
    row.funcOff = funcOff(r, rva);
    if (shapes(r, o, rva, sec, row, r.mask, 0)) return row;
    // fuzzy: displacements, immediates and branch operands wildcarded too; taken only when unique
    const flo = Math.max(sec[0], rva - CAP), fz = { ...row };
    if (shapes(r, o, rva, sec, fz, fuzzyMask(r, flo, Math.min(sec[1], rva + CAP), rva), flo) && fz.count === 1) { fz.kind = "offset-changed"; return fz; }
    return row;  // mapAll sets the final kind from the field diffs
}

// shapes: [before, after] growth; centered first, then one side only (a change right at the site)
function shapes(r: Pe, o: Pe, rva: number, sec: [number, number], row: Row, m: Uint8Array, mb: number): boolean {
    for (const [b0, a0] of [[8, 16], [0, 24], [24, 0]]) {
        let b = b0, a = a0, cands: number[] | null = null, last: number[] = [];
        for (;;) {
            const lo = Math.max(sec[0], rva - b), hi = Math.min(sec[1], rva + a);
            const c = cands === null ? search(r, o, lo, hi, m, mb) : cands.map((x) => x + (lo - row.lo)).filter((x) => verify(r, o, lo, hi, x, m, mb));
            if (c.length === 0) break;
            cands = c; last = c; row.lo = lo; row.hi = hi;
            if (c.length === 1 || (b >= CAP || b0 === 0) && (a >= CAP || a0 === 0)) break;
            b = b0 ? Math.min(CAP, b * 2) : 0; a = a0 ? Math.min(CAP, a * 2) : 0;
        }
        if (last.length) {
            row.count = last.length; row.cands = last.slice(0, MAXCAND).map((x) => x + (rva - row.lo));
            if (last.length === 1) {
                row.new = o.base + row.cands[0];
                row.kind = r.img.subarray(row.lo, row.hi).equals(o.img.subarray(last[0], last[0] + row.hi - row.lo)) ? "exact" : "masked";
            } else row.kind = "ambiguous";
            return true;
        }
    }
    return false;
}

// neighbour tie-break / hint: delta of the nearest unique rows on both sides (cands = RVAs in the other exe)
export function tieBreak(rows: Row[], obase: number) {
    const uniq = rows.filter((x) => x.kind === "exact" || x.kind === "masked" || x.kind === "offset-changed").sort((a, b) => a.ref - b.ref);
    const near = (va: number) => {
        let i = uniq.findIndex((u) => u.ref > va);
        if (i < 0) i = uniq.length;
        return [uniq[i - 1], uniq[i]].filter(Boolean).map((u) => u!.new! - u!.ref);
    };
    for (const x of rows) {
        if (x.kind !== "ambiguous" && x.kind !== "missing") continue;
        const ds = near(x.ref);
        if (x.kind === "ambiguous") {
            const hit = x.cands.filter((c) => ds.includes(obase + c - x.ref));
            if (hit.length === 1) { x.kind = "near"; x.new = obase + hit[0]; }
        } else if (ds.length) x.hint = x.ref + ds[0];
    }
}

export type RefVote = { ref: number; new: number; votes: number; conflicts: number };

export function mapAll(r: Pe, o: Pe, sites: Site[]): { rows: Row[]; refs: RefVote[] } {
    const rows = sites.map((s) => s.va - r.base < r.img.length && sectionOf(r, s.va - r.base) ? mapSite(r, o, s) : { ref: s.va, new: null, kind: "not-code", count: 0, lo: 0, hi: 0, cands: [], funcOff: "?", label: s.label, where: s.where } as Row);
    tieBreak(rows, o.base);
    // pointers read by matched code: relocated dwords and call/jmp targets inside the matched window
    const votes = new Map<number, Map<number, number>>();
    const vote = (a: number, b: number) => { const m = votes.get(a) ?? new Map(); m.set(b, (m.get(b) ?? 0) + 1); votes.set(a, m); };
    for (const x of rows) {
        if (x.new === null) continue;
        const d = x.new - o.base - (x.ref - r.base);
        // field diffs over the window plus the site's own first bytes (a one-sided window can end at the site)
        const rva = x.ref - r.base;
        x.diffs = fieldDiffs(r, o, Math.min(x.lo, rva), Math.min(sectionOf(r, rva)![1], Math.max(x.hi, rva + 16)), d, rva);
        if (x.diffs.length) x.kind = "offset-changed";
        else if (x.kind === "offset-changed") x.kind = "masked";
        for (let i = x.lo; i + 4 <= x.hi; i++) {
            if (r.relocs.has(i)) vote(r.img.readUInt32LE(i), o.img.readUInt32LE(i + d));
            else if (i > x.lo && r.mask[i] && !r.mask[i - 1] && (r.img[i - 1] === 0xe8 || r.img[i - 1] === 0xe9))
                vote(r.base + i + 4 + r.img.readInt32LE(i), o.base + i + d + 4 + o.img.readInt32LE(i + d));
        }
    }
    const refs: RefVote[] = [...votes].map(([a, m]) => {
        const best = [...m].sort((p, q) => q[1] - p[1])[0];
        return { ref: a, new: best[0], votes: best[1], conflicts: [...m.values()].reduce((s, v) => s + v, 0) - best[1] };
    }).sort((a, b) => a.ref - b.ref);
    for (const x of rows) if (x.kind === "not-code") { const v = refs.find((v) => v.ref === x.ref); if (v) { x.kind = "ref"; x.new = v.new; x.count = v.votes; } }
    return { rows, refs };
}

const hex = (a: number | null | undefined) => (a === null || a === undefined ? "" : "0x" + a.toString(16).toUpperCase().padStart(8, "0"));

export function defaultSites(): Site[] {
    const src = join(repo, "native", "ts3native");
    const hits = readdirSync(src).filter((f) => f.endsWith(".cpp") && !/test/.test(f)).flatMap((f) => scan(readFileSync(join(src, f), "utf8"), f.replace(/\.cpp$/, "")));
    const by = new Map<number, Site>();
    for (const h of hits) if (!by.has(h.addr)) by.set(h.addr, { va: h.addr, label: h.label, where: h.where });
    return [...by.values()].sort((a, b) => a.va - b.va);
}

export function readAddrs(text: string): Site[] {
    return text.split(/\r?\n/).map((l) => l.replace(/#.*/, "").trim()).filter(Boolean).map((l, i) => {
        const [a, ...rest] = l.split(/[\s,]+/);
        return { va: parseInt(a, 16), label: rest.join(" "), where: `line ${i + 1}` };
    }).filter((s) => !isNaN(s.va));
}

// struct offset groups and vtable slot shifts (tools/exe-port-diff.ts): one summary line each plus problem rows;
// full lists in <base>.offsets.csv and <base>.vtables.csv
function portReport(r: Pe, o: Pe, rows: Row[], refs: RefVote[], base: string, all: boolean) {
    const groups = groupDiffs(rows.flatMap((x) => x.diffs ?? []));
    writeFileSync(base + ".offsets.csv", "kind,old,new,count,sites\n" + groups.map((g) => [g.kind, g.old, g.new, g.count, g.sites.map(hex).join(" ")].join(",")).join("\n") + "\n");
    for (const g of groups.slice(0, all ? Infinity : 20)) console.log(`offset ${groupLine(g)} x${g.count} at ${g.sites.slice(0, 4).map(hex).join(" ")}${g.sites.length > 4 ? " ..." : ""}`);
    console.log(`offset diffs: ${groups.reduce((s, g) => s + g.count, 0)} instructions in ${groups.length} groups -> ${base}.offsets.csv`);
    const known = new Map<number, number>();
    for (const x of rows) if (x.new !== null && x.kind !== "ref" && x.kind !== "not-code") known.set(x.ref, x.new);
    for (const x of refs) if (!x.conflicts && !known.has(x.ref)) known.set(x.ref, x.new);
    const ds = rows.filter((x) => x.kind === "exact" || x.kind === "masked").map((x) => x.new! - x.ref).sort((a, b) => a - b);
    const vm = mapVtables(r, o, known, ds.length ? ds[ds.length >> 1] : 0);
    // vtables our sources name (a literal anywhere inside one: its start or a slot)
    const named = new Map<number, Set<number>>();
    for (const f of sourceFiles()) for (const x of scanText(readFileSync(join(repo, "native", "ts3native", f), "utf8"), f)) {
        if (x.sect === ".text") continue;
        const v = vm.find((v) => x.addr >= v.ref && x.addr < v.ref + 4 * v.slots && (x.addr - v.ref) % 4 === 0);
        if (v) named.set(v.ref, (named.get(v.ref) ?? new Set()).add(x.addr));
    }
    const line = (v: typeof vm[number]) => {
        const slots = [...(named.get(v.ref) ?? [])].sort((a, b) => a - b).map((a) => {
            const i = (a - v.ref) / 4, s = v.slotShift[i];
            return `${hex(a)} slot ${i} -> ${v.new === null || s === null ? "?" : hex(v.new + 4 * (i + s))}`;
        });
        return `vtable ${hex(v.ref)} -> ${hex(v.new) || "?"} slots ${v.slots}/${v.newSlots} votes ${v.votes}${v.ties ? ` ties ${v.ties}` : ""}: ${shiftRuns(v.slotShift)}${slots.length ? "; named " + slots.join(", ") : ""}`;
    };
    const bad = (v: typeof vm[number]) => v.new === null || v.slots !== v.newSlots || v.slotShift.some((s) => s !== 0)
        || [...(named.get(v.ref) ?? [])].some((a) => v.slotShift[(a - v.ref) / 4] === null);
    writeFileSync(base + ".vtables.csv", "ref,new,slots,newSlots,votes,ties,named,shifts\n" + vm.map((v) =>
        [hex(v.ref), hex(v.new), v.slots, v.newSlots, v.votes, v.ties, [...(named.get(v.ref) ?? [])].map(hex).join(" "), `"${shiftRuns(v.slotShift)}"`].join(",")).join("\n") + "\n");
    const used = vm.filter((v) => named.has(v.ref));
    for (const v of all ? used : used.filter(bad)) console.log(line(v));
    console.log(`vtables: ${vm.length} in ref, ${vm.filter((v) => v.new !== null).length} mapped, ${vm.filter(bad).length} shifted or unmapped; named by sources ${used.length}: ${used.filter(bad).length} shifted or unmapped -> ${base}.vtables.csv`);
}

// ---- self-check fixture: minimal PE32 with .text, .data, .reloc ----
export function makePe(text: Buffer, relocRvas: number[], base = 0x400000): Buffer {
    const blocks: Buffer[] = [];
    const pages = [...new Set(relocRvas.map((r) => r & ~0xfff))].sort((a, b) => a - b);
    for (const pg of pages) {
        const ents = relocRvas.filter((r) => (r & ~0xfff) === pg).map((r) => 0x3000 | (r & 0xfff));
        if (ents.length & 1) ents.push(0);
        const b = Buffer.alloc(8 + ents.length * 2);
        b.writeUInt32LE(pg, 0); b.writeUInt32LE(b.length, 4);
        ents.forEach((e, i) => b.writeUInt16LE(e, 8 + i * 2));
        blocks.push(b);
    }
    const reloc = Buffer.concat(blocks);
    const al = (n: number, a: number) => Math.ceil(n / a) * a;
    const textRva = 0x1000, dataRva = al(textRva + text.length, 0x1000), relRva = dataRva + 0x1000;
    const secs = [[".text", textRva, text, 0x60000020], [".data", dataRva, Buffer.alloc(0x200), 0xc0000040], [".reloc", relRva, reloc, 0x42000040]] as const;
    let raw = 0x400;
    const parts: Buffer[] = [];
    const h = Buffer.alloc(0x400);
    h.write("MZ", 0); h.writeUInt32LE(0x40, 0x3c); h.writeUInt32LE(0x4550, 0x40);
    h.writeUInt16LE(0x14c, 0x44); h.writeUInt16LE(secs.length, 0x46); h.writeUInt16LE(224, 0x54);
    const opt = 0x58;
    h.writeUInt16LE(0x10b, opt); h.writeUInt32LE(base, opt + 28); h.writeUInt32LE(0x1000, opt + 32); h.writeUInt32LE(0x200, opt + 36);
    h.writeUInt32LE(al(relRva + reloc.length, 0x1000), opt + 56); h.writeUInt32LE(0x400, opt + 60); h.writeUInt32LE(16, opt + 92);
    h.writeUInt32LE(relRva, opt + 96 + 40); h.writeUInt32LE(reloc.length, opt + 96 + 44);
    secs.forEach(([name, rva, data, ch], i) => {
        const s = opt + 224 + i * 40, rs = al(data.length, 0x200);
        h.write(name, s); h.writeUInt32LE(data.length, s + 8); h.writeUInt32LE(rva, s + 12); h.writeUInt32LE(rs, s + 16); h.writeUInt32LE(raw, s + 20); h.writeUInt32LE(ch, s + 36);
        const d = Buffer.alloc(rs); data.copy(d); parts.push(d); raw += rs;
    });
    return Buffer.concat([h, ...parts]);
}

function selfCheck(refPath?: string) {
    let seed = 12345;
    const rnd = () => (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 16 & 0xff;
    const N = 0x6000, ref = Buffer.alloc(N);
    for (let i = 0; i < N; i++) ref[i] = rnd();
    for (let i = 0; i < N; i++) if (ref[i] === 0xe8 || ref[i] === 0xe9) ref[i] = 0x90;  // no stray calls in the random fill
    const dup = ref.subarray(0x3000, 0x3400);
    dup.copy(ref, 0x4000);                         // same 1 KB block twice in ref: ambiguous beyond the window cap
    // ref: 8 pointer dwords into .data (0x407000 = .data of the ref), one call at 0x2000 -> 0x800 (before the insert)
    const ptrs = [0x500, 0x900, 0x1800, 0x2500, 0x2800, 0x5200, 0x5600, 0x5a00];
    ptrs.forEach((p, i) => ref.writeUInt32LE(0x407000 + i * 16, p));
    ref[0x2000] = 0xe8; ref.writeInt32LE(0x800 - 0x2005, 0x2001);
    // 26 x `mov eax,[ecx+disp32]` at 0x2B84; the one at 0x2BFC (site) reads +0x918
    for (let k = 0; k < 26; k++) Buffer.from([0x8b, 0x81, 0, 0, 0, 0]).copy(ref, 0x2b84 + 6 * k), ref.writeUInt32LE(k === 20 ? 0x918 : 0x100 + 4 * k, 0x2b86 + 6 * k);
    // other: 0x40 bytes inserted at 0x1000, 0x80 bytes changed around 0x5800 (site there = missing),
    // .data one page up (pointers relocated)
    const ins = 0x40;
    const other = Buffer.concat([ref.subarray(0, 0x1000), Buffer.alloc(ins, 0x90), ref.subarray(0x1000)]);
    const shift = (o: number) => (o >= 0x1000 ? o + ins : o);
    ptrs.forEach((p, i) => other.writeUInt32LE(0x408000 + i * 16, shift(p)));
    other.writeInt32LE(0x800 - shift(0x2005), shift(0x2001));
    for (let i = 0x57c0; i < 0x5840; i++) other[shift(i)] ^= 0x5a;
    other.writeUInt32LE(0x91c, shift(0x2b86 + 6 * 20)); other.writeUInt32LE(0x348, shift(0x2b86 + 6 * 18));  // field offsets moved on both sides of the site
    const otherFull = Buffer.concat([other, Buffer.alloc(0x1000, 0xcc)]);  // pushes .data one page up
    const r = parsePe(makePe(ref, ptrs.map((p) => 0x1000 + p)));
    const o = parsePe(makePe(otherFull, ptrs.map((p) => 0x1000 + shift(p))));
    const va = (off: number) => 0x401000 + off;
    const sites = [0x300, 0x904, 0x1f00, 0x2003, 0x2500, 0x2bfc, 0x3100, 0x5800].map((off) => ({ va: va(off), label: "", where: "" }));
    const { rows, refs } = mapAll(r, o, sites);
    const get = (off: number) => rows.find((x) => x.ref === va(off))!;
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`exe-addr-map self-check FAILED: ${what}`, rows.map((x) => `${hex(x.ref)} ${x.kind} ${hex(x.new)}`)); process.exit(1); } };
    ok(get(0x300).kind === "exact" && get(0x300).new === va(0x300), "before insert = exact, same address");
    ok(get(0x904).kind === "masked" && get(0x904).new === va(0x904), "relocated pointer next to site = masked");
    ok(get(0x1f00).new === va(0x1f40) && get(0x1f00).kind === "exact", "after insert = +0x40");
    ok(get(0x2003).new === va(0x2043) && get(0x2003).kind === "masked", "inside a call operand = masked +0x40");
    ok(get(0x3100).kind === "near" && get(0x3100).new === va(0x3140), "duplicated block resolved by neighbour delta");
    ok(get(0x5800).kind === "missing" && get(0x5800).hint === va(0x5840), "destroyed bytes = missing with hint");
    const oc = get(0x2bfc), og = groupDiffs(rows.flatMap((x) => x.diffs ?? [])).map(groupLine);
    ok(oc.kind === "offset-changed" && oc.new === va(0x2c3c), "displacements changed both sides = offset-changed (fuzzy)");
    ok(og.includes("disp 0x918 -> 0x91C") && og.includes("disp 0x148 -> 0x348") && og.length === 2, `offset groups (${og.join("; ")})`);
    ok(rows.filter((x) => x !== oc).every((x) => !x.diffs?.length), "no offset diffs elsewhere");
    ok(refs.some((v) => v.ref === 0x407010 && v.new === 0x408010 && v.conflicts === 0), "data pointer mapped (.data +0x1000)");
    ok(refs.some((v) => v.ref === va(0x800) && v.new === va(0x800)), "call target mapped");
    const alone = mapAll(r, o, [{ va: va(0x3100), label: "", where: "" }]).rows[0];
    ok(alone.kind === "ambiguous" && alone.count === 2, "duplicated block without neighbours = ambiguous 2");
    ok(mapAll(r, o, [{ va: 0x407010, label: "", where: "" }, ...sites]).rows[0].new === 0x408010, "--addrs data address answered from refs");
    ok(readAddrs("# c\n0x401000 Foo bar\n\n00402000,x").map((s) => s.va).join() === [0x401000, 0x402000].join(), "readAddrs");
    console.log("exe-addr-map self-check: synthetic ok");
    const real = refPath ?? join(repo, "game-copy", "bin", "TS3W.exe");
    if (!existsSync(real)) { console.log("exe-addr-map self-check: real exe absent, skipped"); return; }
    const pe = parsePe(readFileSync(real));
    const some = defaultSites().slice(0, 40);
    const id = mapAll(pe, pe, some).rows;
    const same = id.filter((x) => x.new === x.ref).length;
    ok(same >= some.length - 2, `identity on the Steam exe: ${same}/${some.length}`);
    ok(id.every((x) => !x.diffs?.length), "identity: no offset diffs");
    console.log(`exe-addr-map self-check: Steam identity ${same}/${some.length} ok`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { ref: { type: "string" }, other: { type: "string" }, addrs: { type: "string" }, out: { type: "string" }, all: { type: "boolean" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck(v.ref);
    else {
        if (!v.other) { console.error("usage: node tools/exe-addr-map.ts --other <exe> [--ref <Steam TS3W.exe>] [--addrs file] [--out file.csv|.json] [--all]"); process.exit(2); }
        const r = parsePe(readFileSync(v.ref ?? join(repo, "game-copy", "bin", "TS3W.exe"))), o = parsePe(readFileSync(v.other));
        const sites = v.addrs ? readAddrs(readFileSync(v.addrs, "utf8")) : defaultSites();
        const { rows, refs } = mapAll(r, o, sites);
        const out = v.out ?? join(repo, "out", "exe-addr-map", basename(v.other).replace(/\.\w+$/, "") + ".csv");
        mkdirSync(dirname(out), { recursive: true });
        if (out.endsWith(".json")) writeFileSync(out, JSON.stringify({ sites: rows.map(({ lo, hi, cands, ...x }) => ({ ...x, window: hi - lo })), refs }, null, 1));
        else writeFileSync(out, ["ref,new,kind,count,delta,window,funcOff,hint,label,where",
            ...rows.map((x) => [hex(x.ref), hex(x.new), x.kind, x.count, x.new === null ? "" : x.new - x.ref, x.hi - x.lo, x.funcOff, hex(x.hint), `"${x.label.replace(/"/g, '""')}"`, x.where].join(",")),
            ...refs.map((x) => [hex(x.ref), hex(x.new), x.conflicts ? "ref-conflict" : "ref", x.votes, x.new - x.ref, "", "", "", "", ""].join(","))].join("\n") + "\n");
        const kinds = new Map<string, number>();
        for (const x of rows) kinds.set(x.kind, (kinds.get(x.kind) ?? 0) + 1);
        const problems = rows.filter((x) => v.all || !(x.kind === "exact" || x.kind === "masked"));
        for (const x of problems.slice(0, v.all ? Infinity : 40)) console.log(`${hex(x.ref)} ${x.kind}${x.count > 1 ? " " + x.count : ""} ${hex(x.new) || (x.hint ? "hint " + hex(x.hint) : "")} ${x.label} ${x.where}`.trimEnd());
        if (!v.all && problems.length > 40) console.log(`... ${problems.length - 40} more problem rows in the map file`);
        const identity = rows.filter((x) => x.new === x.ref).length;
        console.log(`${rows.length} addresses: ${[...kinds].map(([k, n]) => `${k} ${n}`).join(", ")}; same address ${identity}; refs ${refs.length} (${refs.filter((x) => x.conflicts).length} with conflicts) -> ${out}`);
        portReport(r, o, rows, refs, out.replace(/\.(csv|json)$/, ""), !!v.all);
    }
}
