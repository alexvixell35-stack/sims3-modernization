// Hashed signatures of our native sites in the Steam TS3W.exe 1.67 for the player-side port probe
// (tools/port-probe/Sims3PortProbe.ps1, docs/68 §7.4). A player with the EA app / Origin exe runs the probe at the main
// menu; it matches these against the game's decrypted code in memory and writes a report of addresses and numbers only.
// tools/port-report.ts turns that report into the tools/exe-addr-map.ts CSV.
//
//   node tools/port-sigs.ts [--ref <Steam TS3W.exe>] [--out tools/port-probe/port-sigs.txt]   build (commit the file)
//   node tools/port-sigs.ts --self-check
//
// Scheme (no game bytes in the file, so the repo and the release can ship it):
//  - sites = the exe-addr-map default list (every code address our native sources name) plus the patch-bytes-check
//    expectation addresses; window = 256 bytes on each side, clipped to the site's code section.
//  - mask = call/jmp rel32 into code (as exe-addr-map) plus absolute pointers (decoded absolute displacements, imm32
//    inside the image): the Steam exe has no .reloc, and the other build's data moved.
//  - tiles = the concrete runs of the union of all windows cut into non-overlapping 16-byte tiles from each run start;
//    tile key = 48 bits from two murmur3-32 hashes. 48 bits per 16 bytes (3 bits per byte, below x86 code entropy)
//    and no overlapping tiles: the bytes cannot be rebuilt from the file.
//  - fields = positions, sizes and kinds of the decoded operands in the windows (no values): r call/jmp rel32,
//    a absolute pointer, d displacement, i immediate.
//  - matcher (here in TS as the reference, in C# inside the probe; both must give the same lines): index every 16-byte
//    window of the other code whose key is a tile key (more than 64 hits = not an anchor); candidates from every anchor
//    tile of the site's window; tiles in order of distance from the site filter the candidates (a tile no candidate
//    has is a mismatch and is skipped) until one is left and the +-32-byte core is checked.
//  - report lines: `S <site> u <new VA> <lo> <hi> <mismatched tiles|->`, `S <site> a <count> <VA...>` (first 16),
//    `S <site> -`; for unique sites `F <site> <field> <hex>`: call targets and absolute pointers inside [lo, hi) or the
//    site's first 16 bytes, displacement/immediate values inside mismatched tiles.
// shortcut: no fuzzy re-search like exe-addr-map's; a site whose every anchor tile changed comes out missing (hint from
//   neighbours in port-report). Upgrade if a real EA report has many missing rows.
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { createHash } from "node:crypto";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { parsePe, defaultSites, makePe } from "./exe-addr-map.ts";
import { insStarts, type Pe } from "./exe-port-diff.ts";
import { decode } from "./x86-len.ts";
import { extract, readSources, sourceTexts } from "./patch-bytes-check.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
export const SIG_FILE = join(repo, "tools", "port-probe", "port-sigs.txt");
export const CAP = 256, K = 16, CORE = 32, MAXHIT = 64, MAXCAND = 16;
export const S1 = 0x53334d31, S2 = 0x706f7274;  // "S3M1", "port"

export type Tile = { rva: number; key: number };
export type Field = { rva: number; size: number; kind: string; ins: number };
export type SigSite = { va: number; tlo: number; thi: number; flo: number; fhi: number };
export type Sig = { steam: string; base: number; tiles: Tile[]; fields: Field[]; sites: SigSite[]; notCode: number[] };

// murmur3 x86_32 over exactly 16 bytes at `at`
export function mm3(b: Uint8Array, at: number, seed: number): number {
    let h = seed >>> 0;
    for (let i = 0; i < 16; i += 4) {
        let k = b[at + i] | (b[at + i + 1] << 8) | (b[at + i + 2] << 16) | (b[at + i + 3] << 24);
        k = Math.imul(k, 0xcc9e2d51); k = (k << 15) | (k >>> 17); k = Math.imul(k, 0x1b873593);
        h ^= k; h = (h << 13) | (h >>> 19); h = (Math.imul(h, 5) + 0xe6546b64) | 0;
    }
    h ^= 16;
    h ^= h >>> 16; h = Math.imul(h, 0x85ebca6b); h ^= h >>> 13; h = Math.imul(h, 0xc2b2ae35); h ^= h >>> 16;
    return h >>> 0;
}
export const key48 = (b: Uint8Array, at: number) => mm3(b, at, S1) * 65536 + (mm3(b, at, S2) & 0xffff);
// test vector shared with the probe's -SelfCheck: key of bytes 0..15
export const KEY_VECTOR = () => key48(Uint8Array.from({ length: 16 }, (_, i) => i), 0);

const lower = <T extends { rva: number }>(a: T[], x: number) => { let l = 0, h = a.length; while (l < h) { const m = (l + h) >> 1; if (a[m].rva < x) l = m + 1; else h = m; } return l; };

export function buildSigs(pe: Pe, vas: number[], steam = ""): Sig {
    const secOf = (rva: number) => pe.exec.find(([a, b]) => rva >= a && rva < b);
    const wins: { va: number; lo: number; hi: number }[] = [], notCode: number[] = [];
    for (const va of [...new Set(vas)].sort((a, b) => a - b)) {
        const rva = va - pe.base, s = rva >= 0 && rva < pe.img.length ? secOf(rva) : undefined;
        if (!s) { notCode.push(va); continue; }
        wins.push({ va, lo: Math.max(s[0], rva - CAP), hi: Math.min(s[1], pe.img.length, rva + CAP) });
    }
    const mask = pe.mask.slice(), fm = new Map<number, Field>();
    const inImg = (v: number) => v >= pe.base && v < pe.base + pe.img.length;
    for (const w of wins) {
        for (let i = w.lo + 1; i + 4 <= w.hi; i++)
            if (pe.mask[i] && !pe.mask[i - 1] && (pe.img[i - 1] === 0xe8 || pe.img[i - 1] === 0xe9) && !fm.has(i)) fm.set(i, { rva: i, size: 4, kind: "r", ins: 1 });
        for (const s of insStarts(pe.img, w.lo, w.hi, w.va - pe.base)) {
            const d = decode(pe.img, s, w.hi)!;
            const add = (off: number, size: number, kind: string) => {
                const at = s + off;
                if (pe.mask[at] || fm.has(at)) return;
                fm.set(at, { rva: at, size, kind, ins: off });
                if (kind === "a") mask.fill(1, at, at + size);
            };
            if (d.dispSize) add(d.dispOff, d.dispSize, d.abs && d.dispSize === 4 ? "a" : "d");
            if (d.immSize) add(d.immOff, d.immSize, d.immSize === 4 && inImg(pe.img.readUInt32LE(s + d.immOff)) ? "a" : "i");
        }
    }
    const iv: [number, number][] = [];
    for (const w of [...wins].sort((a, b) => a.lo - b.lo)) {
        const last = iv[iv.length - 1];
        if (last && w.lo <= last[1]) last[1] = Math.max(last[1], w.hi); else iv.push([w.lo, w.hi]);
    }
    const tiles: Tile[] = [];
    for (const [lo, hi] of iv) for (let i = lo; i < hi;) {
        if (mask[i]) { i++; continue; }
        let j = i; while (j < hi && !mask[j]) j++;
        for (let t = i; t + K <= j; t += K) tiles.push({ rva: t, key: key48(pe.img, t) });
        i = j;
    }
    const fields = [...fm.values()].sort((a, b) => a.rva - b.rva);
    const sites = wins.map((w) => ({ va: w.va, tlo: lower(tiles, w.lo), thi: lower(tiles, w.hi - K + 1), flo: lower(fields, w.lo), fhi: lower(fields, w.hi - 3) }));
    return { steam, base: pe.base, tiles, fields, sites, notCode };
}

const h = (n: number) => n.toString(16).toUpperCase();
export function formatSig(s: Sig): string {
    return [
        "# Sims3 port signatures v1 (tools/port-sigs.ts): Steam TS3W.exe 1.67 site windows as 48-bit hashes of non-overlapping 16-byte tiles, operand positions and addresses. No game bytes.",
        `steam ${s.steam || "-"} base ${h(s.base)} cap ${CAP} k ${K} core ${CORE}`,
        `T ${s.tiles.length}`, ...s.tiles.map((t) => `${h(t.rva)} ${h(t.key)}`),
        `F ${s.fields.length}`, ...s.fields.map((f) => `${h(f.rva)} ${f.size}${f.kind}${h(f.ins)}`),
        `S ${s.sites.length}`, ...s.sites.map((x) => `${h(x.va)} ${x.tlo} ${x.thi} ${x.flo} ${x.fhi}`),
        `N ${s.notCode.length}`, ...s.notCode.map(h), ""].join("\n");
}
// id of a signature file: first 8 hex of sha256 over the text without CR (git may check it out with CRLF)
export const sigId = (text: string) => createHash("sha256").update(text.replace(/\r/g, ""), "utf8").digest("hex").slice(0, 8);

export function parseSig(text: string): Sig {
    const L = text.replace(/\r/g, "").split("\n").filter((l) => l && !l.startsWith("#"));
    const head = L[0].split(" ");
    if (head[0] !== "steam" || +head[5] !== CAP || +head[7] !== K || +head[9] !== CORE) throw new Error("not a v1 port signature file");
    const s: Sig = { steam: head[1], base: parseInt(head[3], 16), tiles: [], fields: [], sites: [], notCode: [] };
    let i = 1;
    const sect = (tag: string) => { const [t, n] = L[i++].split(" "); if (t !== tag) throw new Error(`port-sigs: expected ${tag}, got ${t}`); const r = L.slice(i, i + +n); i += +n; return r; };
    s.tiles = sect("T").map((l) => { const [a, k] = l.split(" "); return { rva: parseInt(a, 16), key: parseInt(k, 16) }; });
    s.fields = sect("F").map((l) => { const [a, f] = l.split(" "); return { rva: parseInt(a, 16), size: +f[0], kind: f[1], ins: parseInt(f.slice(2), 16) }; });
    s.sites = sect("S").map((l) => { const [a, ...n] = l.split(" "); return { va: parseInt(a, 16), tlo: +n[0], thi: +n[1], flo: +n[2], fhi: +n[3] }; });
    s.notCode = sect("N").map((l) => parseInt(l, 16));
    return s;
}

// ---- reference matcher (the probe's C# must produce the same lines) ----
export type Img = { img: Uint8Array; exec: [number, number][]; prefBase: number; runBase: number };
const has = (b: Uint8Array, p: number, key: number) => p >= 0 && p + K <= b.length && key48(b, p) === key;
const i32 = (b: Uint8Array, p: number) => b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
const u = (b: Uint8Array, p: number, n: number) => { let v = 0; for (let i = n - 1; i >= 0; i--) v = v * 256 + b[p + i]; return v; };

export function matchImage(im: Img, sig: Sig): string[] {
    const b = im.img, len = b.length, want = new Set<number>(), h1s = new Set<number>();
    for (const t of sig.tiles) { want.add(t.key); h1s.add(Math.floor(t.key / 65536)); }
    const hits = new Map<number, number[]>();
    for (const [a, e] of im.exec) for (let p = a, end = Math.min(e, len); p + K <= end; p++) {
        const h1 = mm3(b, p, S1);
        if (!h1s.has(h1)) continue;
        const k = h1 * 65536 + (mm3(b, p, S2) & 0xffff);
        if (!want.has(k)) continue;
        const l = hits.get(k) ?? [];
        if (l.length <= MAXHIT) l.push(p);
        hits.set(k, l);
    }
    const norm = (v: number) => (im.runBase !== im.prefBase && v >= im.runBase && v < im.runBase + len ? v - im.runBase + im.prefBase : v) >>> 0;
    const out: string[] = [];
    let nu = 0, na = 0, nm = 0;
    sig.sites.forEach((s, si) => {
        const rva = s.va - sig.base, off = (t: number) => sig.tiles[t].rva - rva;
        const ts: number[] = [];
        for (let t = s.tlo; t < s.thi; t++) ts.push(t);
        ts.sort((x, y) => Math.abs(2 * off(x) + K) - Math.abs(2 * off(y) + K) || off(x) - off(y));
        const core = (t: number) => off(t) < CORE && off(t) + K > -CORE;
        const cand = new Set<number>();
        for (const t of ts) {
            const l = hits.get(sig.tiles[t].key);
            if (l && l.length <= MAXHIT) for (const p of l) { const c = p - off(t); if (c >= 0 && c < len) cand.add(c); }
        }
        if (!cand.size) { out.push(`S ${si} -`); nm++; return; }
        let c = [...cand].sort((x, y) => x - y);
        const mis: number[] = [];
        let lo = 0, hi = 0;
        for (let i = 0; i < ts.length; i++) {
            const t = ts[i], o = off(t), f = c.filter((x) => has(b, x + o, sig.tiles[t].key));
            if (f.length) c = f; else mis.push(t);
            if (i === 0) { lo = o; hi = o + K; } else { lo = Math.min(lo, o); hi = Math.max(hi, o + K); }
            if (c.length === 1 && (i + 1 >= ts.length || !core(ts[i + 1]))) break;
        }
        if (c.length > 1) { out.push(`S ${si} a ${c.length} ${c.slice(0, MAXCAND).map((x) => h(im.prefBase + x)).join(" ")}`); na++; return; }
        nu++;
        const n = c[0];
        mis.sort((x, y) => x - y);
        out.push(`S ${si} u ${h(im.prefBase + n)} ${lo} ${hi} ${mis.length ? mis.join(",") : "-"}`);
        for (let f = s.flo; f < s.fhi; f++) {
            const F = sig.fields[f], o = F.rva - rva, p = n + o;
            if (p < 0 || p + F.size > len) continue;
            let v: number;
            if (F.kind === "r" || F.kind === "a") {
                if (!((o >= lo && o + F.size <= hi) || (o >= 0 && o < 16))) continue;
                v = F.kind === "r" ? (im.prefBase + p + 4 + i32(b, p)) >>> 0 : norm(u(b, p, 4));
            } else {
                if (!mis.some((t) => o < off(t) + K && o + F.size > off(t))) continue;
                v = u(b, p, F.size);
            }
            out.push(`F ${si} ${f} ${h(v)}`);
        }
    });
    out.push(`summary: sites ${sig.sites.length} unique ${nu} ambiguous ${na} missing ${nm}`);
    return out;
}

export function siteVas(): number[] {
    // generated probe headers (vcallsites*.h: thousands of ProbeSnap sites) stay out, as in addr-codemod
    const exp = extract(readSources(sourceTexts())).exp.filter((e) => !/vcallsites/.test(e.where)).map((e) => e.addr);
    return [...new Set([...defaultSites().map((s) => s.va), ...exp])].sort((a, b) => a - b);
}
const defaultRef = () => join(repo, "game-copy", "bin", "TS3W.exe");
export const steamId = (buf: Buffer) => createHash("sha256").update(buf).digest("hex").slice(0, 16);

function selfCheck(refPath?: string) {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`port-sigs self-check FAILED: ${what}`); process.exit(1); } };
    ok(h(KEY_VECTOR()) === KEY_HEX, `key vector ${h(KEY_VECTOR())} (probe -SelfCheck expects ${KEY_HEX})`);
    // synthetic: random code with calls; other = 0x40 bytes inserted at 0x1000, region around 0x4800 destroyed,
    // one displacement in a tile next to 0x2100 changed
    let seed = 99;
    const rnd = () => (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 16 & 0xff;
    const N = 0x6000, ref = Buffer.alloc(N);
    for (let i = 0; i < N; i++) { ref[i] = rnd(); if (ref[i] === 0xe8 || ref[i] === 0xe9) ref[i] = 0x90; }
    ref[0x2000] = 0xe8; ref.writeInt32LE(0x800 - 0x2005, 0x2001);                 // call into code before the insert
    ref.fill(0x90, 0x2108, 0x2110); Buffer.from([0x8b, 0x81, 0x18, 0x09, 0, 0]).copy(ref, 0x2110);                // mov eax,[ecx+0x918]
    const ins = 0x40, sh = (o: number) => (o >= 0x1000 ? o + ins : o);
    const other = Buffer.concat([ref.subarray(0, 0x1000), Buffer.alloc(ins, 0xcc), ref.subarray(0x1000), Buffer.alloc(0x1000)]);
    other.writeInt32LE(0x800 - sh(0x2005), sh(0x2001));
    for (let i = 0x4500; i < 0x4b00; i++) other[sh(i)] ^= 0x5a;
    other.writeUInt16LE(0x91c, sh(0x2112));
    const r = parsePe(makePe(ref, [])), o = parsePe(makePe(other, []));
    const va = (x: number) => 0x401000 + x;
    const sig = parseSig(formatSig(buildSigs(r, [0x300, 0x2003, 0x2100, 0x4800].map(va).concat([0x407010]))));
    ok(sig.notCode.length === 1 && sig.sites.length === 4 && sig.tiles.length > 50, "sites, tiles, not-code");
    ok(sig.fields.some((f) => f.kind === "r" && f.rva === 0x1001 + 0x2000) && sig.fields.some((f) => f.kind === "d" && f.rva === 0x1000 + 0x2112), "call and displacement fields");
    const id = matchImage({ img: r.img, exec: r.exec, prefBase: r.base, runBase: r.base }, sig);
    ok(id.filter((l) => / u /.test(l)).length === 4 && id.every((l) => !/ u /.test(l) || / u (\w+) .* -$/.test(l)), `identity: 4 unique, no mismatches (${id.join(" | ")})`);
    const m = matchImage({ img: o.img, exec: o.exec, prefBase: o.base, runBase: o.base }, sig);
    const S = (i: number) => m.find((l) => l.startsWith(`S ${i} `))!;
    ok(S(0).startsWith(`S 0 u ${h(va(0x300))} `) && S(0).endsWith(" -"), `before insert: same address (${S(0)})`);
    ok(S(1).startsWith(`S 1 u ${h(va(0x2043))} `), `after insert: +0x40 (${S(1)})`);
    ok(m.includes(`F 1 ${sig.fields.findIndex((f) => f.kind === "r")} ${h(va(0x800))}`), "call target reported");
    ok(S(2).startsWith(`S 2 u ${h(va(0x2140))} `) && !S(2).endsWith(" -"), `changed displacement: unique with a mismatched tile (${S(2)})`);
    ok(m.some((l) => l.startsWith("F 2 ") && l.endsWith(" 91C")), "changed displacement value reported");
    ok(S(3) === "S 3 -", `destroyed: missing (${S(3)})`);
    console.log("port-sigs self-check: synthetic ok");
    const real = refPath ?? defaultRef();
    if (!existsSync(real)) { console.log("port-sigs self-check: real exe absent, skipped"); return; }
    if (existsSync(SIG_FILE)) {
        const buf = readFileSync(real), have = parseSig(readFileSync(SIG_FILE, "utf8"));
        ok(have.steam === steamId(buf), `committed signature file is for this Steam exe (${have.steam})`);
        const fresh = formatSig(buildSigs(parsePe(buf), siteVas(), steamId(buf)));
        console.log(`port-sigs self-check: committed file ${sigId(readFileSync(SIG_FILE, "utf8"))}${sigId(fresh) === sigId(readFileSync(SIG_FILE, "utf8")) ? " current" : " STALE (sources changed: rebuild with node tools/port-sigs.ts)"}`);
    }
}
export const KEY_HEX = "4DB95ECCF470";

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { ref: { type: "string" }, out: { type: "string" }, "self-check": { type: "boolean" }, vector: { type: "boolean" } } });
    if (v.vector) console.log(h(KEY_VECTOR()));
    else if (v["self-check"]) selfCheck(v.ref);
    else {
        const buf = readFileSync(v.ref ?? defaultRef()), pe = parsePe(buf);
        const sig = buildSigs(pe, siteVas(), steamId(buf)), text = formatSig(sig), out = v.out ?? SIG_FILE;
        mkdirSync(dirname(out), { recursive: true });
        writeFileSync(out, text);
        console.log(`port-sigs: ${sig.sites.length} code sites, ${sig.notCode.length} not in code, ${sig.tiles.length} tiles, ${sig.fields.length} fields, ${(text.length / 1024).toFixed(0)} KB, id ${sigId(text)} -> ${out}`);
        console.log("AI needed: none");
    }
}
