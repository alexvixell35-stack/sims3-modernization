// sigmap: carry our ts3native hook sites from the Steam TS3W.exe 1.67 onto another build (EA app TS3.exe 1.69) by masked signature.
//
// Steps (EA app 1.69 .text is encrypted on disk, so the second image must come from memory):
//   1. node tools/sigmap.ts gen [--exe game-copy/bin/TS3W.exe] [--sites sites.json] [--out docs/data/sigmap-steam.json]
//        Sites = every code address in native/ts3native/*.cpp (tools/hook-sites.ts scan), or --sites JSON
//        [{"va":"0x4E16F0","feature":"x","where":"file.cpp:12"}]. Per site: a masked signature that is unique in the Steam
//        executable sections. Masked: bytes that are absolute addresses (.reloc entries, or any 4-byte value inside the image),
//        and rel32 operands of E8/E9/0F 8x whose target lies in the image. Tries forward windows first, then windows that start
//        before the site (offset = site - window start). Window 16..64 bytes.
//   2. On the laptop with the EA app game running (start TS3.exe, reach the main menu so the code is decrypted):
//        python tools/probe/dump-image.py --out probe-out/ts3-ea169.bin      (writes the .bin and .bin.json)
//   3. node tools/sigmap.ts match --image probe-out/ts3-ea169.bin [--name ea169]
//        Reads the memory image (layout from <image>.json next to it, or --mem), scans executable sections, and writes
//        docs/data/sigmap-<name>.json with per site unique / ambiguous / none, the 1.69 VA, and coverage per feature.
//   node tools/sigmap.ts --self-check      synthetic PE buffers, no game files
// shortcut: no x86 length decoder, so a window ending inside an instruction keeps that instruction's tail literal;
// a site is reported "unique" only by match count, so verify the mapped VA by hand before a hook relies on it.
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { readdirSync } from "node:fs";
import { scan } from "./hook-sites.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const MIN_LEN = 16, MAX_LEN = 64;
const BACK = [0, 4, 8, 16, 24, 32];

type Sec = { name: string; va: number; vsize: number; raw: number; rsize: number; exec: boolean };
type Pe = { imageBase: number; sizeOfImage: number; timestamp: number; secs: Sec[]; relocs: Set<number>; mem: boolean };
type Sig = { sig: string; mask: string; offset: number };

export function parsePe(b: Buffer, mem: boolean, base?: number): Pe {
    if (b.readUInt16LE(0) !== 0x5a4d) throw new Error("not a PE (no MZ)");
    const pe = b.readUInt32LE(0x3c);
    if (b.readUInt32LE(pe) !== 0x4550) throw new Error("no PE signature");
    const nsec = b.readUInt16LE(pe + 6), optSize = b.readUInt16LE(pe + 20), opt = pe + 24;
    const imageBase = base ?? b.readUInt32LE(opt + 28), sizeOfImage = b.readUInt32LE(opt + 56);
    const secs: Sec[] = [];
    for (let i = 0; i < nsec; i++) {
        const o = opt + optSize + i * 40;
        secs.push({
            name: b.toString("latin1", o, o + 8).replace(/\0.*$/, ""), vsize: b.readUInt32LE(o + 8), va: b.readUInt32LE(o + 12),
            rsize: b.readUInt32LE(o + 16), raw: b.readUInt32LE(o + 20), exec: (b.readUInt32LE(o + 36) & 0x20000000) !== 0,
        });
    }
    const relocs = new Set<number>();
    const rva = b.readUInt32LE(opt + 96 + 5 * 8), sz = b.readUInt32LE(opt + 96 + 5 * 8 + 4);
    const p = { imageBase, sizeOfImage, timestamp: b.readUInt32LE(pe + 8), secs, relocs, mem };
    const rb = rva && sz ? secBytes0(b, p, rva, sz) : null;
    if (rb) for (let o = 0; o + 8 <= rb.length;) {
        const page = rb.readUInt32LE(o), blk = rb.readUInt32LE(o + 4);
        if (blk < 8) break;
        for (let e = o + 8; e + 2 <= o + blk; e += 2) { const w = rb.readUInt16LE(e); if (w >> 12 === 3) relocs.add(page + (w & 0xfff)); }
        o += blk;
    }
    return p;
}
function secBytes0(b: Buffer, p: Pe, rva: number, len: number): Buffer | null {
    if (p.mem) return rva + len <= b.length ? b.subarray(rva, rva + len) : null;
    const s = p.secs.find((s) => rva >= s.va && rva < s.va + Math.max(s.vsize, s.rsize));
    return s ? b.subarray(s.raw + rva - s.va, s.raw + rva - s.va + len) : null;
}
export function secData(b: Buffer, p: Pe, s: Sec): Buffer {
    if (p.mem) return b.subarray(s.va, Math.min(b.length, s.va + s.vsize));
    return b.subarray(s.raw, s.raw + Math.min(s.rsize, s.vsize || s.rsize));
}
const execSecs = (b: Buffer, p: Pe) => p.secs.filter((s) => s.exec).map((s) => ({ s, d: secData(b, p, s) }));

// mask[i]=1 -> byte compared, 0 -> wildcard
export function makeMask(w: Buffer, winRva: number, p: Pe): Uint8Array {
    const m = new Uint8Array(w.length).fill(1), lo = p.imageBase, hi = p.imageBase + p.sizeOfImage;
    const wild = (i: number) => { for (let k = 0; k < 4; k++) m[i + k] = 0; };
    for (let i = 0; i + 4 <= w.length; i++) {
        const v = w.readUInt32LE(i);
        if (p.relocs.has(winRva + i) || (v >= lo && v < hi)) wild(i);
    }
    for (let i = 0; i < w.length; i++) {
        let at = -1;
        if (w[i] === 0xe8 || w[i] === 0xe9) at = i + 1;
        else if (w[i] === 0x0f && i + 1 < w.length && (w[i + 1] & 0xf0) === 0x80) at = i + 2;
        if (at < 0 || at + 4 > w.length) continue;
        const t = p.imageBase + winRva + at + 4 + w.readInt32LE(at);
        if (t >= lo + 0x1000 && t < hi) wild(at);
    }
    return m;
}

// start indexes where sig/mask matches d
export function findAll(d: Buffer, sig: Buffer, mask: Uint8Array, limit = 8): number[] {
    let bs = 0, bl = 0;
    for (let i = 0, s = -1; i <= sig.length; i++) {
        if (i < sig.length && mask[i]) { if (s < 0) s = i; } else if (s >= 0) { if (i - s > bl) { bl = i - s; bs = s; } s = -1; }
    }
    const out: number[] = [];
    if (!bl) return out;
    const anchor = sig.subarray(bs, bs + bl);
    for (let at = d.indexOf(anchor); at >= 0 && out.length < limit; at = d.indexOf(anchor, at + 1)) {
        const st = at - bs;
        if (st < 0 || st + sig.length > d.length) continue;
        let ok = true;
        for (let i = 0; i < sig.length; i++) if (mask[i] && d[st + i] !== sig[i]) { ok = false; break; }
        if (ok) out.push(st);
    }
    return out;
}
function countAll(b: Buffer, p: Pe, sig: Buffer, mask: Uint8Array, limit = 8): { n: number; vas: number[] } {
    const vas: number[] = [];
    for (const { s, d } of execSecs(b, p)) for (const i of findAll(d, sig, mask, limit)) vas.push(p.imageBase + s.va + i);
    return { n: vas.length, vas };
}

export function genSig(b: Buffer, p: Pe, va: number): (Sig & { unique: boolean }) | null {
    const rva = va - p.imageBase;
    const s = p.secs.find((s) => s.exec && rva >= s.va && rva < s.va + s.vsize);
    if (!s) return null;
    const d = secData(b, p, s);
    let best: (Sig & { unique: boolean }) | null = null;
    for (const back of BACK) {
        const start = rva - s.va - back;
        if (start < 0) continue;
        for (let len = Math.max(MIN_LEN, back + 8); len <= MAX_LEN && start + len <= d.length; len += 2) {
            const w = d.subarray(start, start + len), mask = makeMask(w, rva - back, p);
            if (mask.reduce((a, x) => a + x, 0) < 8) continue; // mostly wildcards: not a signature
            const r = countAll(b, p, w, mask, 2);
            if (r.n === 1) return { sig: w.toString("hex"), mask: Buffer.from(mask).toString("hex"), offset: back, unique: true };
            if (!best) best = { sig: w.toString("hex"), mask: Buffer.from(mask).toString("hex"), offset: back, unique: false };
        }
    }
    return best;
}

export function matchSig(b: Buffer, p: Pe, g: Sig): { status: "unique" | "ambiguous" | "none"; n: number; va?: number } {
    const r = countAll(b, p, Buffer.from(g.sig, "hex"), Buffer.from(g.mask, "hex"), 20);
    if (r.n === 0) return { status: "none", n: 0 };
    if (r.n === 1) return { status: "unique", n: 1, va: r.vas[0] + g.offset };
    return { status: "ambiguous", n: r.n };
}

const hex = (a: number) => "0x" + a.toString(16).toUpperCase().padStart(8, "0");
function arg(n: string, d?: string) { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; }

function sitesFromSources(): { va: number; feature: string; where: string }[] {
    const src = join(repo, "native", "ts3native"), m = new Map<number, { feature: Set<string>; where: Set<string> }>();
    for (const f of readdirSync(src).filter((f) => f.endsWith(".cpp") && !/test/.test(f)))
        for (const h of scan(readFileSync(join(src, f), "utf8"), f.replace(/\.cpp$/, ""))) {
            const e = m.get(h.addr) ?? { feature: new Set(), where: new Set() };
            e.feature.add(h.module); e.where.add(h.where); m.set(h.addr, e);
        }
    return [...m].sort((a, b) => a[0] - b[0]).map(([va, e]) => ({ va, feature: [...e.feature].join("+"), where: [...e.where].join(" ") }));
}

function gen() {
    const exe = arg("--exe", join(repo, "game-copy", "bin", "TS3W.exe"))!;
    const b = readFileSync(exe), p = parsePe(b, false);
    const sites = arg("--sites") ? (JSON.parse(readFileSync(arg("--sites")!, "utf8")) as any[]).map((s) => ({ ...s, va: typeof s.va === "string" ? parseInt(s.va, 16) : s.va })) : sitesFromSources();
    const out: any[] = [], skipped: string[] = [];
    for (const s of sites) {
        const g = genSig(b, p, s.va);
        if (!g) { skipped.push(hex(s.va)); out.push({ site: hex(s.va), va: hex(s.va), feature: s.feature, where: s.where, unique: false, skipped: "not in an executable section" }); continue; }
        out.push({ site: hex(s.va), va: hex(s.va), rva: hex(s.va - p.imageBase), sig: g.sig, mask: g.mask, offset: g.offset, unique: g.unique, feature: s.feature, where: s.where });
    }
    const dest = arg("--out", join(repo, "docs", "data", "sigmap-steam.json"))!;
    writeFileSync(dest, JSON.stringify({ source: basename(exe), timestamp: hex(p.timestamp), imageBase: hex(p.imageBase), sites: out }, null, 1) + "\n");
    const u = out.filter((o) => o.unique).length;
    console.log(`${out.length} sites: ${u} unique, ${out.length - u - skipped.length} not unique, ${skipped.length} skipped${skipped.length ? " (" + skipped.join(" ") + ")" : ""} -> ${dest}`);
}

function match() {
    const img = arg("--image");
    if (!img) throw new Error("match needs --image");
    const meta = existsSync(img + ".json") ? JSON.parse(readFileSync(img + ".json", "utf8")) : null;
    const mem = process.argv.includes("--mem") || !!meta;
    const b = readFileSync(img), p = parsePe(b, mem, meta ? parseInt(meta.base, 16) : undefined);
    const sigs = JSON.parse(readFileSync(arg("--sigs", join(repo, "docs", "data", "sigmap-steam.json"))!, "utf8"));
    const name = arg("--name", basename(img).replace(/\.[^.]*$/, ""))!;
    const per: Record<string, Record<string, number>> = {}, tot: Record<string, number> = { unique: 0, ambiguous: 0, none: 0, nosig: 0 };
    const res = sigs.sites.map((s: any) => {
        const r = s.sig ? matchSig(b, p, s) : { status: "nosig" as const, n: 0 };
        tot[r.status]++;
        for (const f of String(s.feature).split("+")) { per[f] ??= { unique: 0, ambiguous: 0, none: 0, nosig: 0 }; per[f][r.status]++; }
        return { site: s.site, feature: s.feature, steamUnique: s.unique, status: r.status, hits: r.n, va: (r as any).va !== undefined ? hex((r as any).va) : undefined };
    });
    writeFileSync(join(repo, "docs", "data", `sigmap-${name}.json`), JSON.stringify({ image: basename(img), timestamp: hex(p.timestamp), imageBase: hex(p.imageBase), totals: tot, perFeature: per, sites: res }, null, 1) + "\n");
    console.log(`${res.length} sites: ${tot.unique} unique, ${tot.ambiguous} ambiguous, ${tot.none} none, ${tot.nosig} without signature`);
    for (const [f, c] of Object.entries(per).sort()) console.log(`  ${f.padEnd(24)} unique ${c.unique}  ambiguous ${c.ambiguous}  none ${c.none}  nosig ${c.nosig}`);
}

// synthetic 32-bit PE: one exec .text at rva 0x1000 holding `text`, image base 0x400000
export function buildPe(text: Buffer, imageSize = 0x10000): Buffer {
    const b = Buffer.alloc(0x400 + text.length);
    b.writeUInt16LE(0x5a4d, 0); b.writeUInt32LE(0x80, 0x3c);
    b.writeUInt32LE(0x4550, 0x80); b.writeUInt16LE(1, 0x86); b.writeUInt16LE(224, 0x94);
    const opt = 0x98; b.writeUInt32LE(0x400000, opt + 28); b.writeUInt32LE(imageSize, opt + 56);
    const sh = opt + 224;
    b.write(".text", sh); b.writeUInt32LE(text.length, sh + 8); b.writeUInt32LE(0x1000, sh + 12);
    b.writeUInt32LE(text.length, sh + 16); b.writeUInt32LE(0x400, sh + 20); b.writeUInt32LE(0x60000020, sh + 36);
    text.copy(b, 0x400);
    return b;
}

function selfCheck() {
    const ok = (c: boolean, m: string) => { if (!c) throw new Error("sigmap self-check failed: " + m); };
    const rnd = (n: number, seed: number) => { const o = Buffer.alloc(n); let x = seed; for (let i = 0; i < n; i++) { x = (x * 1103515245 + 12345) >>> 0; o[i] = (x >>> 16) & 0xff; } return o; };
    // text with a unique function at 0x200: ... call rel32 -> 0x1800 (rva), mov eax,[abs 0x500000]
    const mk = (shift: number) => {
        const t = Buffer.alloc(0x2000); rnd(0x2000, 7).copy(t);
        for (let i = 0; i < t.length; i++) if (t[i] === 0xe8 || t[i] === 0xe9) t[i] = 0x90; // no stray call opcodes
        const f = Buffer.from("55 8B EC 83 EC 20 53 56 57 E8 00 00 00 00 A1 00 00 50 00 8B 4D 08 33 C0 89 45 FC 5F 5E 5B C9 C3 CC".replace(/ /g, ""), "hex");
        const at = 0x200 + shift;
        f.copy(t, at);
        const target = 0x1800 + shift; // rva of callee, moves with the code
        t.writeInt32LE(target - (0x1000 + at + 9 + 5), at + 10);
        return { t, at };
    };
    const a = mk(0), pa = parsePe(buildPe(a.t), false);
    const ba = buildPe(a.t), site = 0x400000 + 0x1000 + a.at;
    const g = genSig(ba, pa, site)!;
    ok(!!g && g.unique, "unique sig not found");
    const sg = Buffer.from(g.sig, "hex"), mk2 = Buffer.from(g.mask, "hex");
    ok(mk2.includes(0), "no wildcards (rel32/abs not masked)");
    // shifted copy: code moved by 0x40, call target moved too, abs address unchanged value differs (rebased)
    const sh = mk(0x40), bs = buildPe(sh.t), ps = parsePe(bs, false);
    const r = matchSig(bs, ps, g);
    ok(r.status === "unique" && r.va === 0x400000 + 0x1000 + sh.at, `shifted match ${r.status} ${r.va}`);
    // ambiguous: duplicate the function body (identical bytes, same rel target)
    const d = Buffer.from(a.t); const body = Buffer.from(d.subarray(a.at, a.at + 33));
    body.copy(d, 0x900);
    const rd = matchSig(buildPe(d), parsePe(buildPe(d), false), { sig: Buffer.from("55 8B EC 83 EC 20 53 56 57".replace(/ /g, ""), "hex").toString("hex"), mask: "ff".repeat(9), offset: 0 });
    ok(rd.status === "ambiguous" && rd.n === 2, `ambiguous ${rd.status} ${rd.n}`);
    // none
    const rn = matchSig(ba, pa, { sig: "deadbeefcafe", mask: "ff".repeat(6), offset: 0 });
    ok(rn.status === "none", "none");
    // memory layout: image with headers at 0, text at rva 0x1000
    const mem = Buffer.alloc(0x1000 + a.t.length); buildPe(a.t).copy(mem, 0, 0, 0x400); a.t.copy(mem, 0x1000);
    const pm = parsePe(mem, true);
    ok(matchSig(mem, pm, g).status === "unique", "memory layout match");
    console.log("sigmap self-check ok");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const cmd = process.argv[2];
    if (process.argv.includes("--self-check")) selfCheck();
    else if (cmd === "gen") gen();
    else if (cmd === "match") match();
    else { console.log("usage: node tools/sigmap.ts gen|match|--self-check (see header)"); process.exitCode = 1; }
}
