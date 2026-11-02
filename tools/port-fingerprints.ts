// Structural fingerprints for the player-side port probe (tools/port-probe/Sims3PortProbe.ps1, docs/68 §7.4): what
// the probe reports besides the site map, so tools/port-report.ts can place Steam sites the byte match missed and diff
// the script assemblies, without any game bytes leaving the player's PC. Reference implementation: the probe's C# must
// produce the same lines (`node tools/port-report.ts --self-check --ps` compares both on the Steam exe).
//
//   node tools/port-fingerprints.ts [--ref <Steam TS3W.exe>] [--asm <dir of .dll|.package>]   print counts and sizes
//   node tools/port-fingerprints.ts --self-check
//
// Exe lines (pointers normalised to the preferred image base):
//   P <start> <size> <insns> <h0,...,h11> <callees|-> <imports|-> <strings|-> <globals|->   one per function
//     starts = call targets that sit after int3/nop/ret padding, vtable slots, IC functions, the entry point;
//     size = up to the next start (max 32 KB); body = linear decode (tools/x86-len.ts) until the end or an invalid op;
//     h = instruction classes mov lea stack call jmp jcc ret cmp alu x87 sse other; imports/strings = indexes into M/Z
//   M <idx> <dll!name>            import slots (by name table; `#n` = ordinal)
//   Z <idx> <VA> <text>           strings code points at: printable ASCII, 4..120 chars (data, not code)
//   R <vtable> <slots> <class>    MSVC RTTI: type descriptor name -> complete object locator -> vtable
//   W <vtable> <slot fn,...>      vtables: runs of >= 2 aligned pointers to function starts outside code
//   I <fn> <slot> <name>          internal-call tables: aligned {function, "Class::Method"} pairs outside code
// Assembly lines (S3SA resources of Game\Bin\Gameplay\*.package):
//   A <module> <mvid> <version> <methods>
//   t <Namespace.Type>            then its methods in token order (token = 0x06000001 + running index in the module):
//   m <il size> <il hash> <params> <Method>     il hash = top 24 bits of FNV-1a 32 over the IL body
// shortcut: nested types print without their enclosing type, and the IL hash includes metadata tokens (a method whose
//   tokens were renumbered shows as changed with the same size); upgrade if a real diff is noisy.
import { readFileSync, readdirSync, existsSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { decode } from "./x86-len.ts";
import { s3saImages } from "./extract-assemblies.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
export type Sec = { name: string; rva: number; size: number; ch: number };
export type Img = { img: Uint8Array; prefBase: number; runBase: number };
const h = (n: number) => n.toString(16).toUpperCase();
const u32 = (b: Uint8Array, p: number) => (b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24)) >>> 0;
const i32 = (b: Uint8Array, p: number) => b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24);
const u16 = (b: Uint8Array, p: number) => b[p] | (b[p + 1] << 8);

export function sections(b: Uint8Array): Sec[] {
    const pe = u32(b, 0x3c), n = u16(b, pe + 6), opt = pe + 24, first = opt + u16(b, pe + 20), out: Sec[] = [];
    for (let i = 0, s = first; i < n; i++, s += 40) {
        let name = ""; for (let j = 0; j < 8 && b[s + j]; j++) name += String.fromCharCode(b[s + j]);
        const vs = u32(b, s + 8), raw = u32(b, s + 16);
        out.push({ name, rva: u32(b, s + 12), size: vs || raw, ch: u32(b, s + 36) });
    }
    return out;
}
const PRE = new Set([0x66, 0x67, 0xf0, 0xf2, 0xf3, 0x26, 0x2e, 0x36, 0x3e, 0x64, 0x65]);

// one instruction class 0..11; q = first opcode byte after prefixes
export function insClass(b: Uint8Array, at: number, op: number): number {
    let q = at; while (PRE.has(b[q]) && q - at < 14) q++;
    const reg = (b[q + 1] >> 3) & 7;
    if (op < 0x100) {
        if ((op >= 0x88 && op <= 0x8c) || op === 0x8e || op === 0xc6 || op === 0xc7 || (op >= 0xa0 && op <= 0xa3) || (op >= 0xb0 && op <= 0xbf)) return 0;
        if (op === 0x8d) return 1;
        if ((op >= 0x50 && op <= 0x61) || op === 0x68 || op === 0x6a || op === 0x8f || (op === 0xff && reg === 6)) return 2;
        if (op === 0xe8 || (op === 0xff && (reg === 2 || reg === 3))) return 3;
        if (op === 0xe9 || op === 0xeb || (op === 0xff && (reg === 4 || reg === 5))) return 4;
        if ((op >= 0x70 && op <= 0x7f) || (op >= 0xe0 && op <= 0xe3)) return 5;
        if (op === 0xc2 || op === 0xc3) return 6;
        if ((op >= 0x38 && op <= 0x3d) || op === 0x84 || op === 0x85 || op === 0xa8 || op === 0xa9 || ((op === 0xf6 || op === 0xf7) && reg === 0) || (op >= 0x80 && op <= 0x83 && reg === 7)) return 7;
        if ((op < 0x40 && (op & 7) < 6) || (op >= 0x40 && op <= 0x4f) || (op >= 0x80 && op <= 0x83) || op === 0xc0 || op === 0xc1 || (op >= 0xd0 && op <= 0xd3) || op === 0xf6 || op === 0xf7 || op === 0x69 || op === 0x6b) return 8;
        if (op >= 0xd8 && op <= 0xdf) return 9;
        return 11;
    }
    if (op >= 0x0f80 && op <= 0x0f8f) return 5;
    if (op === 0x0fb6 || op === 0x0fb7 || op === 0x0fbe || op === 0x0fbf) return 0;
    if (op === 0x0faf) return 8;
    if (op > 0xffff || (op >= 0x0f10 && op <= 0x0f17) || (op >= 0x0f28 && op <= 0x0f2f) || (op >= 0x0f50 && op <= 0x0f7f) || op === 0x0fc2 || op === 0x0fc6 || op >= 0x0fd0) return 10;
    return 11;
}

export function cstr(b: Uint8Array, p: number, max: number): string | null {
    let s = "";
    for (let i = 0; i <= max; i++) {
        if (p + i >= b.length) return null;
        const c = b[p + i];
        if (c === 0) return s;
        if (c < 0x20 || c > 0x7e) return null;
        s += String.fromCharCode(c);
    }
    return null;
}

export function fingerprints(im: Img): string[] {
    const b = im.img, len = b.length, pb = im.prefBase, secs = sections(b);
    const secOf = (rva: number) => secs.find((s) => rva >= s.rva && rva < s.rva + s.size);
    const norm = (v: number) => (im.runBase !== pb && v >= im.runBase && v < im.runBase + len ? v - im.runBase + pb : v) >>> 0;
    const isExec = (rva: number) => { const s = rva >= 0 && rva < len ? secOf(rva) : undefined; return !!s && (s.ch & 0x20000000) !== 0; };
    const dataSecs = secs.filter((s) => !(s.ch & 0x20000000));
    const strAt = (va: number) => { const r = va - pb; if (r < 0 || r >= len || isExec(r) || !secOf(r)) return null; const t = cstr(b, r, 120); return t !== null && t.length >= 4 ? t : null; };
    const pe = u32(b, 0x3c), opt = pe + 24;
    // imports by slot
    const imp = new Map<number, number>(), impNames: string[] = [];
    const idir = u32(b, opt + 96 + 8);
    if (idir) for (let d = idir; d + 20 <= len; d += 20) {
        const oft = u32(b, d), nm = u32(b, d + 12), ft = u32(b, d + 16);
        if (!nm && !ft) break;
        const dll = (cstr(b, nm, 64) ?? "?").toLowerCase(), th = oft || ft;
        for (let i = 0; th + 4 * i + 4 <= len && i < 4096; i++) {
            const e = u32(b, th + 4 * i);
            if (!e) break;
            const name = e & 0x80000000 ? "#" + (e & 0xffff) : e + 2 < len ? cstr(b, e + 2, 120) ?? "?" : "?";
            imp.set(pb + ft + 4 * i, impNames.length); impNames.push(`${dll}!${name}`);
        }
    }
    const fnStart = (r: number) => isExec(r) && (b[r - 1] === 0xcc || b[r - 1] === 0xc3 || b[r - 1] === 0x90 || b[r - 3] === 0xc2);
    // RTTI
    const tds = new Map<number, string>();
    for (const s of dataSecs) for (let p = s.rva, e = Math.min(len, s.rva + s.size) - 4; p < e; p++) {
        if (b[p] === 0x2e && b[p + 1] === 0x3f && b[p + 2] === 0x41 && (b[p + 3] === 0x56 || b[p + 3] === 0x55) && p - 8 >= s.rva) { const n = cstr(b, p, 200); if (n) tds.set(pb + p - 8, n); }
    }
    const cols = new Map<number, string>(), rtti: { vt: number; slots: number[]; name: string }[] = [];
    for (const s of dataSecs) for (let q = s.rva + 12, e = Math.min(len, s.rva + s.size) - 4; q <= e; q += 4) { const n = tds.get(norm(u32(b, q))); if (n !== undefined && u32(b, q - 12) === 0) cols.set(pb + q - 12, n); }
    for (const s of dataSecs) for (let q = s.rva, e = Math.min(len, s.rva + s.size) - 4; q < e; q += 4) {
        const n = cols.get(norm(u32(b, q)));
        if (n === undefined) continue;
        const slots: number[] = [];
        for (let k = q + 4; k + 4 <= Math.min(len, s.rva + s.size) && slots.length < 1024; k += 4) { const v = norm(u32(b, k)); if (!isExec(v - pb)) break; slots.push(v); }
        rtti.push({ vt: pb + q + 4, slots, name: n });
    }
    // internal-call tables
    const ics: [number, string, number][] = [];
    for (const s of dataSecs) for (let q = s.rva, e = Math.min(len, s.rva + s.size) - 8; q <= e; q += 4) {
        const f = norm(u32(b, q));
        if (!isExec(f - pb)) continue;
        const t = strAt(norm(u32(b, q + 4)));
        if (t && t.includes("::")) ics.push([f, t, pb + q]);
    }
    const starts = new Set<number>();
    const execSecs = secs.filter((s) => s.ch & 0x20000000);
    for (const s of execSecs) for (let p = s.rva, e = Math.min(len, s.rva + s.size); p + 5 <= e; p++) if (b[p] === 0xe8) { const t = p + 5 + i32(b, p + 1); if (fnStart(t)) starts.add(t); }
    for (const v of rtti) for (const f of v.slots) if (fnStart(f - pb)) starts.add(f - pb);
    for (const [f] of ics) if (isExec(f - pb)) starts.add(f - pb);
    const ep = u32(b, opt + 16);
    if (ep && isExec(ep)) starts.add(ep);
    // vtables: runs of >= 2 aligned dwords outside code pointing at function starts (as exe-port-diff without .reloc)
    const vts: string[] = [], vfn = new Set<number>();
    for (const s of dataSecs) {
        const lim = Math.min(len, s.rva + s.size);
        let cur: number[] = [], at = 0;
        for (let q = s.rva; q + 4 <= lim; q += 4) {
            const v = norm(u32(b, q));
            if (fnStart(v - pb)) { if (!cur.length) at = q; cur.push(v); vfn.add(v - pb); }
            else { if (cur.length >= 2) vts.push(`W ${h(pb + at)} ${cur.map(h).join(",")}`); cur = []; }
        }
        if (cur.length >= 2) vts.push(`W ${h(pb + at)} ${cur.map(h).join(",")}`);
    }
    for (const f of vfn) starts.add(f);
    const st = [...starts].sort((x, y) => x - y), out: string[] = [];
    const strIdx = new Map<number, number>(), strs: string[] = [];
    for (let i = 0; i < st.length; i++) {
        const s = st[i], sec = secOf(s)!, end = Math.min(i + 1 < st.length ? st[i + 1] : len, s + 0x8000, sec.rva + sec.size, len);
        const hist = new Array(12).fill(0), callees = new Set<number>(), imps = new Set<number>(), ss = new Set<number>(), gl = new Set<number>();
        let p = s, n = 0;
        while (p < end) {
            const d = decode(b, p, end);
            if (!d) break;
            n++; hist[insClass(b, p, d.op)]++;
            if (d.op === 0xe8 && d.relSize === 4) { const t = p + d.len + i32(b, p + d.relOff); if (isExec(t)) callees.add(pb + t); }
            const vals: number[] = [];
            if (d.abs && d.dispSize === 4) vals.push(norm(u32(b, p + d.dispOff)));
            if (d.immSize === 4) vals.push(norm(u32(b, p + d.immOff)));
            for (const v of vals) {
                const ii = imp.get(v);
                if (ii !== undefined) { imps.add(ii); continue; }
                const t = strAt(v);
                if (t !== null) { let k = strIdx.get(v); if (k === undefined) { k = strs.length; strIdx.set(v, k); strs.push(`Z ${k} ${h(v)} ${t}`); } ss.add(k); continue; }
                const r = v - pb, sc = r >= 0 && r < len ? secOf(r) : undefined;
                if (sc && (sc.ch & 0x80000000) && !(sc.ch & 0x20000000)) gl.add(v);
            }
            p += d.len;
        }
        const L = (x: Set<number>, hx: boolean) => (x.size ? [...x].sort((a, c) => a - c).map((v) => (hx ? h(v) : String(v))).join(",") : "-");
        out.push(`P ${h(pb + s)} ${end - s} ${n} ${hist.join(",")} ${L(callees, true)} ${L(imps, false)} ${L(ss, false)} ${L(gl, true)}`);
    }
    return [
        ...impNames.map((x, i) => `M ${i} ${x}`), ...strs, ...out,
        ...rtti.map((v) => `R ${h(v.vt)} ${v.slots.length} ${v.name}`), ...vts,
        ...ics.map(([f, t, q]) => `I ${h(f)} ${h(q)} ${t}`),
    ];
}

// ---- assemblies: ECMA-335 tables, enough for MethodDef names, IL bodies, MVID and version ----
const CODED: Record<string, number[]> = {
    TypeDefOrRef: [2, 1, 0x1b], HasConstant: [4, 8, 0x17],
    HasCustomAttribute: [6, 4, 1, 2, 8, 9, 10, 0, 0x0e, 0x17, 0x14, 0x11, 0x1a, 0x1b, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2a, 0x2c, 0x2b],
    HasFieldMarshal: [4, 8], HasDeclSecurity: [2, 6, 0x20], MemberRefParent: [2, 1, 0x1a, 6, 0x1b], HasSemantics: [0x14, 0x17],
    MethodDefOrRef: [6, 0x0a], MemberForwarded: [4, 6], Implementation: [0x26, 0x23, 0x27], CustomAttributeType: [-1, -1, 6, 0x0a, -1],
    ResolutionScope: [0, 0x1a, 0x23, 1], TypeOrMethodDef: [2, 6],
};
// same as tools/managed-callgraph.ts SCHEMA (the probe's C# carries the same table as strings)
export const SCHEMA_TEXT = "0:2 s g g g|1:cResolutionScope s s|2:4 s s cTypeDefOrRef t4 t6|3:t4|4:2 s b|5:t6|6:4 2 2 s b t8|7:t8|8:2 2 s|9:t2 cTypeDefOrRef|10:cMemberRefParent s b|11:2 cHasConstant b|12:cHasCustomAttribute cCustomAttributeType b|13:cHasFieldMarshal b|14:2 cHasDeclSecurity b|15:2 4 t2|16:4 t4|17:b|18:t2 t20|19:t20|20:2 s cTypeDefOrRef|21:t2 t23|22:t23|23:2 s b|24:2 t6 cHasSemantics|25:t2 cMethodDefOrRef cMethodDefOrRef|26:s|27:b|28:2 cMemberForwarded s t26|29:4 t4|30:4 4|31:4|32:4 2 2 2 2 4 b s s|33:4|34:4 4 4|35:2 2 2 2 4 b s s b|36:4 t35|37:4 4 4 t35|38:4 s b|39:4 4 s s cImplementation|40:4 4 s cImplementation|41:t2 t2|42:2 2 cTypeOrMethodDef s|43:cMethodDefOrRef b|44:t42 cTypeDefOrRef";
const SCHEMA: string[][] = [];
for (const t of SCHEMA_TEXT.split("|")) { const [i, c] = t.split(":"); SCHEMA[+i] = c.split(" "); }

export const fnv32 = (b: Uint8Array, p: number, n: number) => { let x = 0x811c9dc5; for (let i = 0; i < n; i++) x = Math.imul(x ^ b[p + i], 0x01000193) >>> 0; return x >>> 0; };

export function methodTable(buf: Uint8Array): string[] {
    const pe = u32(buf, 0x3c), nsec = u16(buf, pe + 6), opt = pe + 24, first = opt + u16(buf, pe + 20);
    const off = (r: number) => { for (let i = 0; i < nsec; i++) { const s = first + 40 * i, va = u32(buf, s + 12), sz = Math.max(u32(buf, s + 8), u32(buf, s + 16)); if (r >= va && r < va + sz) return r - va + u32(buf, s + 20); } return -1; };
    const cli = off(u32(buf, opt + 96 + 14 * 8)), md = off(u32(buf, cli + 8));
    let p = md + 16 + u32(buf, md + 12) + 2;
    const ns = u16(buf, p); p += 2;
    const st: Record<string, number> = {};
    for (let i = 0; i < ns; i++) { const o = u32(buf, p); p += 8; let e = p; while (buf[e]) e++; st[String.fromCharCode(...buf.subarray(p, e))] = md + o; p = (e + 4) & ~3; }
    const t = st["#~"], heap = buf[t + 6], rows = new Array(64).fill(0);
    p = t + 24;
    for (let i = 0; i < 64; i++) if (i < 32 ? (u32(buf, t + 8) >>> i) & 1 : (u32(buf, t + 12) >>> (i - 32)) & 1) { rows[i] = u32(buf, p); p += 4; }
    const tagBits = (n: number) => Math.ceil(Math.log2(n));
    const size = (c: string): number => {
        if (/^\d+$/.test(c)) return +c;
        if (c === "s") return heap & 1 ? 4 : 2;
        if (c === "g") return heap & 2 ? 4 : 2;
        if (c === "b") return heap & 4 ? 4 : 2;
        if (c[0] === "t") return rows[+c.slice(1)] < 0x10000 ? 2 : 4;
        const tabs = CODED[c.slice(1)], mx = Math.max(...tabs.map((x) => (x < 0 ? 0 : rows[x])));
        return mx < 1 << (16 - tagBits(tabs.length)) ? 2 : 4;
    };
    const tab: number[][][] = [];
    for (let ti = 0; ti < 64; ti++) {
        if (!rows[ti]) continue;
        if (!SCHEMA[ti]) throw new Error(`unknown metadata table ${ti}`);
        const sz = SCHEMA[ti].map(size), rs: number[][] = [];
        for (let r = 0; r < rows[ti]; r++) { const row: number[] = []; for (const s of sz) { row.push(s === 1 ? buf[p] : s === 2 ? u16(buf, p) : u32(buf, p)); p += s; } rs.push(row); }
        tab[ti] = rs;
    }
    const str = (i: number) => { let e = st["#Strings"] + i, s = e; while (buf[e]) e++; return Buffer.from(buf.subarray(s, e)).toString("utf8"); };
    const mod = tab[0][0], g = st["#GUID"] + (mod[2] - 1) * 16;
    const mvid = [...buf.subarray(g, g + 16)].map((x) => x.toString(16).padStart(2, "0")).join("");
    const asm = tab[0x20]?.[0], ver = asm ? `${asm[1]}.${asm[2]}.${asm[3]}.${asm[4]}` : "-";
    const types = tab[2] ?? [], meths = tab[6] ?? [], out: string[] = [];
    const blob = (i: number) => { let q = st["#Blob"] + i; const c = buf[q]; q += (c & 0x80) === 0 ? 1 : (c & 0xc0) === 0x80 ? 2 : 4; return q; };
    const cu = (q: number) => { const c = buf[q]; return (c & 0x80) === 0 ? c : (c & 0xc0) === 0x80 ? ((c & 0x3f) << 8) | buf[q + 1] : ((c & 0x1f) << 24) | (buf[q + 1] << 16) | (buf[q + 2] << 8) | buf[q + 3]; };
    types.forEach((ty, ti) => {
        const full = (str(ty[2]) ? str(ty[2]) + "." : "") + str(ty[1]);
        const lo = ty[5] - 1, hi = ti + 1 < types.length ? types[ti + 1][5] - 1 : meths.length;
        if (lo < hi) out.push(`t ${full}`);
        for (let mi = lo; mi < hi && mi < meths.length; mi++) {
            const m = meths[mi], sg = blob(m[4]), params = cu(sg + 1 + ((buf[sg] & 0x10) ? 1 : 0));
            let ilSize = 0, ilHash = 0;
            const bo = m[0] ? off(m[0]) : -1;
            if (bo >= 0) {
                const hb = buf[bo];
                if ((hb & 3) === 2) { ilSize = hb >> 2; ilHash = fnv32(buf, bo + 1, ilSize); }
                else { ilSize = u32(buf, bo + 4); ilHash = fnv32(buf, bo + 4 * (u16(buf, bo) >> 12), ilSize); }
            }
            out.push(`m ${ilSize} ${h(ilHash >>> 8)} ${params} ${str(m[3])}`);
        }
    });
    return [`A ${str(mod[1])} ${mvid} ${ver} ${meths.length}`, ...out];
}

// every script assembly of a folder: .dll files, or S3SA resources of .package files
export function assemblies(dir: string): string[] {
    const out: string[] = [];
    for (const f of readdirSync(dir).sort()) {
        const p = join(dir, f);
        if (/\.dll$/i.test(f)) { try { out.push(...methodTable(readFileSync(p))); } catch { /* not .NET */ } }
        else if (/\.package$/i.test(f)) for (const x of s3saImages(p)) if (x.img) { try { out.push(...methodTable(x.img)); } catch { /* skip */ } }
    }
    return out;
}

// ---- Rodney side: parse fingerprint lines, place problem rows, diff assemblies ----
export type Fn = { start: number; size: number; insns: number; hist: number[]; callees: number[]; imports: string[]; strings: string[]; globals: number[] };
export type Fp = { fns: Fn[]; byStart: Map<number, Fn>; ics: Map<string, number[]>; icSlots: Map<number, string>; rtti: { vt: number; slots: number; name: string }[]; vts: { vt: number; fns: number[] }[] };
export function parseFp(lines: string[]): Fp {
    const imp: string[] = [], str = new Map<number, string>(), fns: Fn[] = [], ics = new Map<string, number[]>(), rtti: Fp["rtti"] = [], vts: Fp["vts"] = [], icSlots = new Map<number, string>();
    const L = (s: string) => (s === "-" ? [] : s.split(","));
    for (const l of lines) {
        if (l[1] !== " ") continue;
        const c = l[0];
        if (c === "M") { const i = l.indexOf(" ", 2); imp[+l.slice(2, i)] = l.slice(i + 1); }
        else if (c === "Z") { const a = l.indexOf(" ", 2), b = l.indexOf(" ", a + 1); str.set(+l.slice(2, a), l.slice(b + 1)); }
        else if (c === "P") {
            const w = l.split(" ");
            fns.push({ start: parseInt(w[1], 16), size: +w[2], insns: +w[3], hist: w[4].split(",").map(Number), callees: L(w[5]).map((x) => parseInt(x, 16)),
                imports: L(w[6]).map((x) => imp[+x]), strings: L(w[7]).map((x) => str.get(+x)!), globals: L(w[8]).map((x) => parseInt(x, 16)) });
        } else if (c === "I") { const w = l.split(" "), n = w.slice(3).join(" "), a = ics.get(n) ?? []; a.push(parseInt(w[1], 16)); ics.set(n, a); icSlots.set(parseInt(w[2], 16), n); }
        else if (c === "R") { const w = l.split(" "); rtti.push({ vt: parseInt(w[1], 16), slots: +w[2], name: w.slice(3).join(" ") }); }
        else if (c === "W") { const w = l.split(" "); vts.push({ vt: parseInt(w[1], 16), fns: w[2].split(",").map((x) => parseInt(x, 16)) }); }
    }
    return { fns, byStart: new Map(fns.map((f) => [f.start, f])), ics, icSlots, rtti, vts };
}
export function fnAt(fp: Fp, va: number): Fn | undefined {
    let lo = 0, hi = fp.fns.length - 1, best: Fn | undefined;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (fp.fns[m].start <= va) { best = fp.fns[m]; lo = m + 1; } else hi = m - 1; }
    return best && va < best.start + best.size ? best : undefined;
}

// structure key of a function: no addresses, no size
export const fnKey = (f: Fn) => `${f.insns}|${f.hist.join(",")}|${f.imports.join(",")}|${f.strings.join("")}|${f.callees.length}`;

export type Cand = { ref: number; fn: number; cand: number | null; score: number; second: number; guess: number | null; why: string };
// function pairs from anchors (IC names, mapped sites at the same function offset, strings only one function uses on
// both sides), then a score per problem site: shared strings 3, mapped callees 3, mapped callers 3, imports 1, similar
// size 1, similar instruction mix 1; taken when >= 6 and 3 ahead of the next
export function placeRows(steam: Fp, other: Fp, mapped: [number, number][], problems: number[]): { pairs: Map<number, number>; cands: Cand[] } {
    const pairs = new Map<number, number>();
    for (const [n, a] of steam.ics) { const b = other.ics.get(n); if (a.length === 1 && b?.length === 1) pairs.set(a[0], b[0]); }
    for (const [s, o] of mapped) { const f = fnAt(steam, s), g = fnAt(other, o); if (f && g && s - f.start === o - g.start && !pairs.has(f.start)) pairs.set(f.start, g.start); }
    const uniq = (fp: Fp) => { const m = new Map<string, Fn[]>(); for (const f of fp.fns) for (const s of new Set(f.strings)) { const a = m.get(s); if (a) a.push(f); else m.set(s, [f]); } return m; };
    const us = uniq(steam), uo = uniq(other);
    for (const [s, fs] of us) { const gs = uo.get(s); if (fs.length === 1 && gs?.length === 1 && !pairs.has(fs[0].start)) pairs.set(fs[0].start, gs[0].start); }
    const callers = (fp: Fp) => { const m = new Map<number, number[]>(); for (const f of fp.fns) for (const c of f.callees) { const a = m.get(c); if (a) a.push(f.start); else m.set(c, [f.start]); } return m; };
    const sc = callers(steam), oc = callers(other);
    // structure key (no addresses, no size): unique on both sides = pair; then pairs spread to callees/callers whose
    // key is unique among the unpaired neighbours on both sides, a few rounds
    const byKey = (fs: Fn[]) => { const m = new Map<string, Fn[]>(); for (const f of fs) { const k = fnKey(f), a = m.get(k); if (a) a.push(f); else m.set(k, [f]); } return m; };
    const ks = byKey(steam.fns), ko = byKey(other.fns), taken = new Set(pairs.values());
    const add = (a: number, b: number) => { if (pairs.has(a) || taken.has(b)) return false; pairs.set(a, b); taken.add(b); return true; };
    for (const [k, fs] of ks) { const gs = ko.get(k); if (fs.length === 1 && gs?.length === 1 && fs[0].insns >= 8) add(fs[0].start, gs[0].start); }
    for (let round = 0; round < 6; round++) {
        let grew = 0;
        for (const [a, b] of [...pairs]) {
            const f = steam.byStart.get(a), g = other.byStart.get(b);
            if (!f || !g) continue;
            for (const [ns, no] of [[f.callees, g.callees], [sc.get(a) ?? [], oc.get(b) ?? []]] as [number[], number[]][]) {
                const us = ns.filter((x) => !pairs.has(x)).map((x) => steam.byStart.get(x)).filter((x): x is Fn => !!x);
                const uo = no.filter((x) => !taken.has(x)).map((x) => other.byStart.get(x)).filter((x): x is Fn => !!x);
                if (us.length === 1 && uo.length === 1) { grew += +add(us[0].start, uo[0].start); continue; }
                const bs = byKey(us), bo = byKey(uo);
                for (const [k, xs] of bs) { const ys = bo.get(k); if (xs.length === 1 && ys?.length === 1) grew += +add(xs[0].start, ys[0].start); }
            }
        }
        if (!grew) break;
    }
    const byImp = new Map<string, Fn[]>();
    for (const g of other.fns) for (const i of new Set(g.imports)) { const a = byImp.get(i); if (a) a.push(g); else byImp.set(i, [g]); }
    const cands: Cand[] = [];
    for (const va of problems) {
        const f = fnAt(steam, va);
        if (!f) { cands.push({ ref: va, fn: 0, cand: null, score: 0, second: 0, guess: null, why: "no function" }); continue; }
        const direct = pairs.get(f.start);
        if (direct !== undefined) { cands.push({ ref: va, fn: f.start, cand: direct, score: 99, second: 0, guess: direct + va - f.start, why: "anchor" }); continue; }
        const pool = new Set<number>();
        for (const s of f.strings) for (const g of uo.get(s) ?? []) pool.add(g.start);
        for (const c of f.callees) { const pc = pairs.get(c); if (pc !== undefined) for (const g of oc.get(pc) ?? []) pool.add(g); }
        for (const c of sc.get(f.start) ?? []) { const pc = pairs.get(c); if (pc !== undefined) for (const g of other.byStart.get(pc)?.callees ?? []) pool.add(g); }
        if (pool.size === 0) for (const i of new Set(f.imports)) if ((byImp.get(i)?.length ?? 99) <= 8) for (const g of byImp.get(i)!) pool.add(g.start);
        const fc = new Set(f.callees.map((c) => pairs.get(c)).filter((x) => x !== undefined));
        const fk = new Set((sc.get(f.start) ?? []).map((c) => pairs.get(c)).filter((x) => x !== undefined));
        const tot = f.hist.reduce((a, b) => a + b, 0) || 1;
        const scored = [...pool].map((s) => {
            const g = other.byStart.get(s);
            if (!g) return { s, v: 0 };
            let v = 3 * f.strings.filter((x) => g.strings.includes(x)).length + f.imports.filter((x) => g.imports.includes(x)).length;
            v += 3 * g.callees.filter((c) => fc.has(c)).length + 3 * (oc.get(g.start) ?? []).filter((c) => fk.has(c)).length;
            if (g.insns >= f.insns * 0.8 && g.insns <= f.insns * 1.25) v++;
            if (f.hist.reduce((a, x, i) => a + Math.abs(x - g.hist[i]), 0) / tot < 0.2) v++;
            return { s, v };
        }).sort((a, b) => b.v - a.v || a.s - b.s);
        const best = scored[0], second = scored[1]?.v ?? 0, okc = !!best && best.v >= 6 && best.v >= second + 3;
        cands.push({ ref: va, fn: f.start, cand: best?.s ?? null, score: best?.v ?? 0, second, guess: okc ? best.s + va - f.start : null, why: okc ? "score" : best ? "weak" : "no candidates" });
    }
    return { pairs, cands };
}

// globals by vote: paired functions with the same number of globals read them in address order
export function mapGlobals(steam: Fp, other: Fp, pairs: Map<number, number>): Map<number, { new: number; votes: number; conflicts: number }> {
    const v = new Map<number, Map<number, number>>();
    for (const [a, b] of pairs) {
        const f = steam.byStart.get(a), g = other.byStart.get(b);
        if (!f || !g || !f.globals.length || f.globals.length !== g.globals.length) continue;
        f.globals.forEach((x, i) => { const m = v.get(x) ?? new Map<number, number>(); m.set(g.globals[i], (m.get(g.globals[i]) ?? 0) + 1); v.set(x, m); });
    }
    const out = new Map<number, { new: number; votes: number; conflicts: number }>();
    for (const [x, m] of v) { const [b, n] = [...m].sort((p, q) => q[1] - p[1] || p[0] - q[0])[0]; out.set(x, { new: b, votes: n, conflicts: [...m.values()].reduce((s, c) => s + c, 0) - n }); }
    return out;
}

export type VtRow = { ref: number; new: number | null; slots: number; newSlots: number; votes: number; shift: (number | null)[] };
// vtables by majority vote of their paired slot functions (vtable, slot shift); slots without a pair take agreeing
// neighbours' shift (tools/exe-port-diff.ts mapVtables, on fingerprint data)
export function mapVts(steam: Fp, other: Fp, pairs: Map<number, number>): VtRow[] {
    const idx = new Map<number, [number, number][]>();
    other.vts.forEach((v, vi) => v.fns.forEach((f, j) => { const a = idx.get(f); if (a) a.push([vi, j]); else idx.set(f, [[vi, j]]); }));
    const ds = [...pairs].map(([a, b]) => b - a).sort((a, b) => a - b), med = ds.length ? ds[ds.length >> 1] : 0;
    // fallback for vtables without votes: the same sequence of slot structure keys, unique on both sides
    const seq = (fp: Fp, fns: number[]) => fns.map((f) => { const x = fp.byStart.get(f); return x ? fnKey(x) : "?"; }).join(";");
    const so = new Map<string, number[]>();
    other.vts.forEach((w, i) => { const k = seq(other, w.fns), a = so.get(k); if (a) a.push(i); else so.set(k, [i]); });
    const ss = new Map<string, number>();
    for (const v of steam.vts) { const k = seq(steam, v.fns); ss.set(k, (ss.get(k) ?? 0) + 1); }
    return steam.vts.map((v) => {
        const votes = new Map<string, number>();
        v.fns.forEach((f, i) => { const g = pairs.get(f), at = g === undefined ? undefined : idx.get(g); if (at && at.length <= 8) for (const [vi, j] of at) votes.set(`${vi}:${j - i}`, (votes.get(`${vi}:${j - i}`) ?? 0) + 1); });
        const row: VtRow = { ref: v.vt, new: null, slots: v.fns.length, newSlots: 0, votes: 0, shift: v.fns.map(() => null) };
        if (!votes.size) {
            const k = seq(steam, v.fns), o = so.get(k);
            if (ss.get(k) === 1 && o?.length === 1) Object.assign(row, { new: other.vts[o[0]].vt, newSlots: v.fns.length, shift: v.fns.map(() => 0) });
            return row;
        }
        const cs = [...votes].map(([k, n]) => { const [vi, sh] = k.split(":").map(Number); return { vi, sh, n }; });
        cs.sort((a, b) => b.n - a.n || Number(other.vts[a.vi].fns.length !== v.fns.length) - Number(other.vts[b.vi].fns.length !== v.fns.length)
            || Math.abs(other.vts[a.vi].vt - v.vt - med) - Math.abs(other.vts[b.vi].vt - v.vt - med) || a.vi - b.vi);
        const { vi, sh, n } = cs[0], w = other.vts[vi];
        Object.assign(row, { new: w.vt, newSlots: w.fns.length, votes: n });
        const s = v.fns.map((f, i) => {
            const g = pairs.get(f); if (g === undefined) return null;
            let best: number | null = null;
            w.fns.forEach((x, j) => { if (x === g && (best === null || Math.abs(j - i - sh) < Math.abs(best - sh))) best = j - i; });
            return best;
        });
        row.shift = s.map((x, i) => {
            if (x !== null) return x;
            let p = i - 1; while (p >= 0 && s[p] === null) p--;
            let q = i + 1; while (q < s.length && s[q] === null) q++;
            const a = p >= 0 ? s[p] : null, c = q < s.length ? s[q] : null;
            return a === null && c === null ? (v.fns.length === w.fns.length ? sh : null) : a === null ? c : c === null || a === c ? a : null;
        });
        return row;
    });
}

export type Asm = { mvid: string; ver: string; methods: Map<string, { size: number; hash: string }> };
export function parseAsms(lines: string[]): Map<string, Asm> {
    const out = new Map<string, Asm>();
    let cur: Asm | null = null, type = "";
    for (const l of lines) {
        if (l.startsWith("A ")) { const w = l.split(" "); cur = { mvid: w[2], ver: w[3], methods: new Map() }; out.set(w[1], cur); }
        else if (l.startsWith("t ") && cur) type = l.slice(2);
        else if (l.startsWith("m ") && cur) {
            const w = l.split(" "), base = `${type}::${w.slice(4).join(" ")}/${w[3]}`;
            let k = base, i = 1;
            while (cur.methods.has(k)) k = `${base}#${++i}`;
            cur.methods.set(k, { size: +w[1], hash: w[2] });
        }
    }
    return out;
}
export type MDiff = { module: string; method: string; status: string; ts3fixes: boolean };
// changed / removed / added methods per module; ts3fixes = type and method name both appear as words in its sources
export function managedDiff(steam: Map<string, Asm>, other: Map<string, Asm>, src: string): { rows: MDiff[]; same: string[] } {
    const words = new Set(src.match(/[A-Za-z_]\w*/g) ?? []), rows: MDiff[] = [], same: string[] = [];
    const used = (k: string) => { const m = k.match(/([^.:]+)::([^/#]+)/); return !!m && words.has(m[1].replace(/`\d+$/, "")) && words.has(m[2].replace(/^\./, "")); };
    for (const [mod, a] of steam) {
        const b = other.get(mod);
        if (!b) { rows.push({ module: mod, method: "*", status: "module-missing", ts3fixes: true }); continue; }
        if (a.mvid === b.mvid) { same.push(mod); continue; }
        for (const [k, x] of a.methods) {
            const y = b.methods.get(k);
            const st = !y ? "removed" : y.size !== x.size ? "size-changed" : y.hash !== x.hash ? "hash-changed" : "";
            if (st) rows.push({ module: mod, method: k, status: st, ts3fixes: used(k) });
        }
        for (const k of b.methods.keys()) if (!a.methods.has(k)) rows.push({ module: mod, method: k, status: "added", ts3fixes: false });
    }
    return { rows, same };
}

function selfCheck() {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`port-fingerprints self-check FAILED: ${what}`); process.exit(1); } };
    ok(insClass(Uint8Array.from([0x8b, 0xc1]), 0, 0x8b) === 0 && insClass(Uint8Array.from([0xff, 0x15]), 0, 0xff) === 3 && insClass(Uint8Array.from([0x0f, 0x84]), 0, 0x0f84) === 5, "instruction classes");
    ok(fnv32(Uint8Array.from([0x61]), 0, 1) === 0xe40c292c, "fnv32 vector");
    ok(cstr(Uint8Array.from([0x41, 0x42, 0]), 0, 10) === "AB" && cstr(Uint8Array.from([0x41, 1, 0]), 0, 10) === null, "cstr");
    // Rodney side on synthetic fingerprints: other = everything +0x100, F3 recompiled (other size), data +0x40
    const H = "3,1,2,1,0,1,1,1,1,0,0,0";
    const fpl = (d: number, f3insns: number, dd: number) => parseFp([
        "M 0 kernel32.dll!Sleep", `Z 0 ${h(0x600000 + dd)} hello world`, `Z 1 ${h(0x600100 + dd)} unique log %d`,
        `P ${h(0x401000 + d)} 64 12 ${H} ${h(0x402000 + d)} 0 0 ${h(0x500000 + dd)}`,
        `P ${h(0x402000 + d)} 32 11 ${H} - - - -`,
        `P ${h(0x403000 + d)} 128 ${f3insns} ${H} ${h(0x402000 + d)} 0 1 -`,
        `P ${h(0x404000 + d)} 16 9 ${H} - - - -`,
        `W ${h(0x700000 + dd)} ${h(0x401000 + d)},${h(0x402000 + d)}`,
        `I ${h(0x404000 + d)} ${h(0x600200 + dd)} ScriptCore.X::Y`]);
    const sf = fpl(0, 30, 0), of = fpl(0x100, 34, 0x40);
    ok(fnAt(sf, 0x403010)?.start === 0x403000 && fnAt(sf, 0x403090) === undefined, "fnAt");
    const pr = placeRows(sf, of, [[0x401004, 0x401104]], [0x403010]);
    ok(pr.pairs.get(0x404000) === 0x404100 && pr.pairs.get(0x401000) === 0x401100, "pairs from IC names and mapped sites");
    const c = pr.cands[0];
    ok(c.guess === 0x403110, `recompiled function placed by strings, imports and mapped callee (${JSON.stringify(c)})`);
    const vm = mapVts(sf, of, pr.pairs);
    ok(vm[0].new === 0x700040 && vm[0].shift.every((x) => x === 0), "vtable by slot pairs");
    ok(mapGlobals(sf, of, pr.pairs).get(0x500000)?.new === 0x500040, "global by paired function");
    ok(of.icSlots.get(0x600240) === "ScriptCore.X::Y", "IC slot");
    const asm = (sz: number) => parseAsms(["A UI.dll 00ff 1.0.0.0 2", "t Sims3.UI.Panel", `m ${sz} ABCDEF 1 Show`, "m 4 123456 0 Hide"]);
    const md = managedDiff(asm(10), parseAsms(["A UI.dll 11ff 1.0.0.0 2", "t Sims3.UI.Panel", "m 12 ABCDEF 1 Show", "m 4 123457 0 Hide"]), "class X { void F() { Panel.Show(); } }");
    ok(md.rows.length === 2 && md.rows[0].status === "size-changed" && md.rows[0].ts3fixes && md.rows[1].status === "hash-changed" && !md.rows[1].ts3fixes, `managed diff (${JSON.stringify(md.rows)})`);
    ok(managedDiff(asm(10), asm(10), "").same.join() === "UI.dll", "same MVID");
    console.log("port-fingerprints self-check: synthetic ok (real exe and probe parity: port-report --self-check)");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { ref: { type: "string" }, asm: { type: "string" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck();
    else {
        const { parsePe } = await import("./exe-addr-map.ts");
        const pe = parsePe(readFileSync(v.ref ?? join(repo, "game-copy", "bin", "TS3W.exe")));
        const t0 = Date.now(), lines = fingerprints({ img: pe.img, prefBase: pe.base, runBase: pe.base });
        const c = (k: string) => lines.filter((l) => l.startsWith(k + " ")).length;
        console.log(`exe: functions ${c("P")}, imports ${c("M")}, strings ${c("Z")}, rtti vtables ${c("R")}, ic pairs ${c("I")}, ${(lines.join("\n").length / 1024).toFixed(0)} KB, ${Date.now() - t0} ms`);
        const dir = v.asm ?? join(repo, "game-copy", "assemblies");
        if (existsSync(dir)) { const a = assemblies(dir); console.log(`assemblies: ${a.filter((l) => l.startsWith("A ")).length} (${basename(dir)}), methods ${a.length}, ${(a.join("\n").length / 1024).toFixed(0)} KB`); }
    }
}
