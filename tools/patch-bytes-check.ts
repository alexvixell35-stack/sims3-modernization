// Offline check of the original bytes ts3native expects at every site it patches or detours, against the Steam 1.67
// exe (self-validation: all must match) or another exe build at the addresses tools/exe-addr-map.ts maps them to.
//
//   node tools/patch-bytes-check.ts [--other <exe>] [--ref <Steam TS3W.exe>] [--out file.csv] [--all]
//   node tools/patch-bytes-check.ts --self-check
//
// Expectations are read statically from native/ts3native (no build):
//  - InstallDetour(name, ADDR, BYTES, N, hook) and Matches(ADDR, BYTES, N) with constant addresses and named byte
//    arrays (also ADDRS[i] with BYTES[i]);
//  - tables of site structs: an address field (at/addr/site) with an expected-bytes field (old/pro/orig/bytes/b/
//    expect/prologue) and an optional len; a target/callee field without bytes = `E8 rel32` to it; slot+fn = the
//    dword at slot holds fn.
// Patched sites without any expectation = docs/68 §2 addresses of a patching kind not covered: "unchecked".
// Kinds: ok (same bytes), ported (differs only in rel32 / absolute pointer operands: the source's expected bytes
// need the other exe's values), mismatch, unmapped (exe-addr-map found no unique address).
// Prints a summary line plus problem rows; every row -> out/patch-bytes-check/<exe>.csv (gitignored, EA bytes).
// shortcut: addresses or bytes built at run time (local variables, computed rel32) are not extracted; they show
// as "unresolved patch calls" (count + list in the CSV) and their sites as unchecked when docs/68 lists them.
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import { join, dirname, basename } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { parsePe, mapAll, makePe } from "./exe-addr-map.ts";
import { decode } from "./x86-len.ts";
import type { Pe } from "./exe-port-diff.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const SRC = join(repo, "native", "ts3native");
type Node = string | Node[];
export type Expect = { addr: number; bytes: number[]; kind: "bytes" | "call" | "slot"; target?: number; where: string; name: string };

// ---- C source reading (best effort) ----
function strip(t: string): string {  // comments -> spaces (line numbers kept), strings kept
    let o = "";
    for (let i = 0; i < t.length;) {
        const c = t[i];
        if (c === "/" && t[i + 1] === "/") { while (i < t.length && t[i] !== "\n") { o += " "; i++; } }
        else if (c === "/" && t[i + 1] === "*") { const e = t.indexOf("*/", i + 2), end = e < 0 ? t.length : e + 2; o += t.slice(i, end).replace(/[^\n]/g, " "); i = end; }
        else if (c === '"' || c === "'") { let j = i + 1; while (j < t.length && t[j] !== c && t[j] !== "\n") j += t[j] === "\\" ? 2 : 1; o += t.slice(i, j + 1); i = j + 1; }
        else { o += c; i++; }
    }
    return o;
}
// value up to the next depth-0 `,` `;` or closing bracket, from pos; braces become nested lists
function parseInit(t: string, p: number): { node: Node; end: number } {
    while (/\s/.test(t[p])) p++;
    if (t[p] === "{") {
        const out: Node[] = [];
        p++;
        for (;;) {
            while (/\s/.test(t[p])) p++;
            if (t[p] === "}" || p >= t.length) return { node: out, end: p + 1 };
            const r = parseInit(t, p);
            out.push(r.node); p = r.end;
            while (/\s/.test(t[p])) p++;
            if (t[p] === ",") p++;
        }
    }
    let d = 0, s = p;
    for (; p < t.length; p++) {
        const c = t[p];
        if (c === '"' || c === "'") { p++; while (p < t.length && t[p] !== c) p += t[p] === "\\" ? 2 : 1; continue; }
        if ("([".includes(c)) d++;
        else if (")]".includes(c)) { if (d === 0) break; d--; }
        else if (d === 0 && ",;}".includes(c)) break;
    }
    return { node: t.slice(s, p).trim(), end: p };
}
function splitArgs(t: string, p: number): string[] {  // p just after `(`
    const out: string[] = [];
    for (;;) { const r = parseInit(t, p); out.push(r.node as string); p = r.end; if (t[p] !== ",") return out; p++; }
}
function cBytes(s: string): number[] {  // "\x55\x8B" string literal
    const b: number[] = [], body = s.slice(1, -1);
    for (let i = 0; i < body.length; i++) {
        if (body[i] !== "\\") { b.push(body.charCodeAt(i)); continue; }
        const m = /^x([0-9a-fA-F]{1,2})|^([0-7]{1,3})/.exec(body.slice(i + 1));
        if (m) { b.push(parseInt(m[1] ?? m[2], m[1] ? 16 : 8)); i += m[0].length; }
        else { b.push({ n: 10, t: 9, r: 13, "0": 0 }[body[i + 1] as "n"] ?? body.charCodeAt(i + 1)); i++; }
    }
    return b;
}

type Sym = { type: string; dims: number; init: Node; file: string; line: number; pos: number; depth: number };
type Ctx = { file: string; pos: number } | undefined;  // where a name is used: locals in scope there win
type Field = { name: string; arr: boolean; ptr: boolean };
export type Src = { syms: Map<string, Sym[]>; structs: Map<string, Field[]>; texts: [string, string][]; depths: Map<string, Int32Array> };

export function readSources(files: [string, string][]): Src {
    const syms = new Map<string, Sym[]>(), depths = new Map<string, Int32Array>(), structs = new Map<string, Field[]>(), texts: [string, string][] = [];
    for (const [file, raw] of files) {
        const t = strip(raw);
        texts.push([file, t]);
        const depth = new Int32Array(t.length + 1);
        for (let i = 0, d = 0; i < t.length; i++) { if (t[i] === "{") d++; else if (t[i] === "}") d--; depth[i + 1] = d; }
        depths.set(file, depth);
        const def = (name: string, s: Sym) => syms.set(name, [...(syms.get(name) ?? []), s]);
        const lineOf = (p: number) => t.slice(0, p).split("\n").length;
        for (const m of t.matchAll(/^[ \t]*#\s*define\s+(\w+)[ \t]+([^\n]+)/gm)) def(m[1], { type: "define", dims: 0, init: m[2].trim(), file, line: lineOf(m.index!), pos: m.index!, depth: 0 });
        for (const m of t.matchAll(/\bstruct\s+(\w+)\s*\{([^{}]*)\}/g)) {
            const fields: Field[] = [];
            for (const decl of m[2].split(";")) {
                const d = decl.replace(/:\s*\d+/g, "").trim().replace(/^(const|volatile|unsigned|signed)\s+/g, "");
                const tm = /^(?:(?:const|volatile|unsigned|signed|struct)\s+)*\w+(?:\s*\*)?\s+([\s\S]+)$/.exec(d);
                if (!tm) continue;
                for (const n of tm[1].split(",")) { const nm = /(\*?)\s*(\w+)\s*(\[)?/.exec(n.trim()); if (nm) fields.push({ name: nm[2], arr: !!nm[3], ptr: !!nm[1] || /\*/.test(d.split(/\s+\w+\s*(\[|,|$)/)[0]) }); }
            }
            if (!structs.has(m[1])) structs.set(m[1], fields);
        }
        const re = /\b((?:(?:static|const|constexpr|volatile)\s+)*)(unsigned\s+char|unsigned\s+int|unsigned\s+long|\w+)\s*(\*?)\s*(\w+)\s*((?:\[[^\]]*\])*)\s*=\s*/g;
        for (let m: RegExpExecArray | null; (m = re.exec(t));) {
            const quals = m[1], type = m[2], name = m[4];
            if (/^(return|else|case|goto|new|delete|typedef)$/.test(type) || /^(if|while|for|switch)$/.test(name)) continue;
            // the assignment must start a declaration: previous non-space char ends a statement or a block
            const before = t.slice(0, m.index).trimEnd().slice(-1);
            if (before && !";{}),".includes(before) && !/^\s*$/.test(t.slice(t.lastIndexOf("\n", m.index), m.index))) continue;
            let r = parseInit(t, re.lastIndex), cur = { name, dims: (m[5].match(/\[/g) ?? []).length };
            for (;;) {
                def(cur.name, { type, dims: cur.dims, init: r.node, file, line: lineOf(m.index), pos: m.index, depth: /static/.test(quals) && depth[m.index] ? 0 : depth[m.index] });
                if (t[r.end] !== ",") break;
                const nx = /^\s*(\*?)\s*(\w+)\s*((?:\[[^\]]*\])*)\s*=\s*/.exec(t.slice(r.end + 1));
                if (!nx) break;
                cur = { name: nx[2], dims: (nx[3].match(/\[/g) ?? []).length };
                r = parseInit(t, r.end + 1 + nx[0].length);
            }
            re.lastIndex = r.end;
        }
    }
    return { syms, structs, texts, depths };
}

// a name at ctx: the latest local still in scope there (same file, block not closed since), else the file-scope
// or function-static one (unity build: one namespace)
// A bare name that is a parameter of the enclosing function (`GnPatch(u8* site, ...)`) is not a symbol (a same-named global
// would win): it stands for the callers' arguments. undefined = not a parameter; [] = a parameter with no caller found.
function paramArgs(src: Src, file: string, pos: number, name: string): { e: string; ctx: Ctx }[] | undefined {
    const t = src.texts.find(([f]) => f === file)?.[1], dp = src.depths.get(file);
    if (!t || !dp) return undefined;
    let b = pos;
    while (b > 0 && dp[b - 1] > 0) b--;  // the outermost body's `{`
    const head = t.slice(0, b - 1).replace(/\s+$/, ""), close = head.lastIndexOf(")");
    if (close < 0 || head.slice(close + 1).replace(/\b(const|noexcept)\b|\s/g, "")) return undefined;
    let d = 0, open = close;
    for (; open >= 0; open--) { if (head[open] === ")") d++; else if (head[open] === "(" && --d === 0) break; }
    const fn = open > 0 ? /(\w+)\s*$/.exec(head.slice(0, open))?.[1] : undefined;
    if (!fn) return undefined;
    const params = splitArgs(head, open + 1).map((x) => x.trim().replace(/\s*=.*$/, ""));
    const idx = params.findIndex((x) => new RegExp("(^|[\\s*&])" + name + "$").test(x));
    if (idx < 0) return undefined;
    const out: { e: string; ctx: Ctx }[] = [];
    for (const [f, tx] of src.texts) for (const m of tx.matchAll(new RegExp("\\b" + fn + "\\s*\\(", "g"))) {
        if (/\b(bool|void|int|DWORD|u8\s*\*?)\s+$/.test(tx.slice(Math.max(0, m.index! - 16), m.index))) continue;  // the definition
        const a = splitArgs(tx, m.index! + m[0].length);
        if (a[idx] !== undefined) out.push({ e: a[idx].trim(), ctx: { file: f, pos: m.index! } });
    }
    return out;
}
export function look(src: Src, name: string, ctx: Ctx): Sym | undefined {
    const defs = src.syms.get(name) ?? [];
    if (ctx) {
        const dp = src.depths.get(ctx.file)!;
        const loc = defs.filter((d) => d.file === ctx.file && d.depth > 0 && d.pos < ctx.pos && (() => { for (let i = d.pos; i < ctx.pos; i++) if (dp[i] < d.depth) return false; return true; })());
        if (loc.length) return loc[loc.length - 1];
        const st = defs.filter((d) => d.file === ctx.file && d.depth === 0 && d.pos < ctx.pos);  // nearest function static first
        if (st.length) return st[st.length - 1];
    }
    return defs.find((d) => d.depth === 0);
}
const BYTE_T = /^(u8|BYTE|uint8_t|unsigned\s+char|char)$/;
export function evalExpr(src: Src, e: string, ctx?: Ctx, seen = new Set<string>()): number | null {
    let s = e.trim();
    for (let k = 0; k < 6; k++) s = s.replace(/\(\s*(?:const\s+|volatile\s+|unsigned\s+)*\w+\s*\**\s*\)(?=\s*[\w(])/g, "");
    s = s.replace(/'(\\?.)'/g, (_, c: string) => String(c.length > 1 ? cBytes(`"${c}"`)[0] : c.charCodeAt(0)));
    s = s.replace(/\bsizeof\s*\(\s*(\w+)\s*\)/g, (all, n: string) => { const v = look(src, n, ctx); const b = v && arrBytes(src, v.init, ctx); return b ? String(b.length * (BYTE_T.test(v!.type) ? 1 : 4)) : all; });
    s = s.replace(/\b(0[xX][0-9a-fA-F]+|\d+)[uUlL]*\b/g, (_, n: string) => String(Number(n)));
    s = s.replace(/\b([A-Za-z_]\w*)\b/g, (n) => {
        const v = look(src, n, ctx);
        if (!v || typeof v.init !== "string" || seen.has(n)) return n;
        const r = evalExpr(src, v.init, v.depth ? { file: v.file, pos: v.pos } : ctx, new Set([...seen, n]));
        return r === null ? n : String(r);
    });
    if (!/^[\d\s+\-*/%&|^~<>()]+$/.test(s)) return null;
    try { const v = Function(`"use strict"; return (${s});`)(); return typeof v === "number" && isFinite(v) ? v : null; } catch { return null; }
}
function arrBytes(src: Src, n: Node, ctx?: Ctx): number[] | null {
    if (typeof n === "string") {
        if (/^".*"$/.test(n)) return cBytes(n);
        const v = look(src, n.trim(), ctx);
        return v && (typeof v.init !== "string" || /^".*"$/.test(v.init)) ? arrBytes(src, v.init, { file: v.file, pos: v.pos }) : null;
    }
    const out: number[] = [];
    for (const x of n) { if (typeof x !== "string") return null; const v = evalExpr(src, x, ctx); if (v === null) return null; out.push(v); }
    return out;
}

// ---- extraction ----
export function extract(src: Src): { exp: Expect[]; unresolved: string[] } {
    const exp: Expect[] = [], unresolved: string[] = [];
    const add = (addr: number | null, bytes: number[] | null, where: string, name: string, len?: number | null) => {
        if (addr === null || !bytes || !bytes.length || bytes.some((b) => b < 0 || b > 255) || (len && len > bytes.length)) return false;
        exp.push({ addr: addr >>> 0, bytes: bytes.slice(0, len && len > 0 ? len : bytes.length), kind: "bytes", where, name });
        return true;
    };
    const call = (addr: number, target: number, where: string, name: string) => {
        const rel = (target - (addr + 5)) | 0;
        exp.push({ addr, bytes: [0xe8, rel & 255, (rel >> 8) & 255, (rel >> 16) & 255, (rel >>> 24) & 255], kind: "call", target, where, name });
    };
    // tables of site structs
    for (const [name, defs] of src.syms) for (const s of defs) {
        const f = src.structs.get(s.type);
        if (!f || s.dims < 1 || typeof s.init === "string") continue;
        const idx = (re: RegExp, pred: (x: Field) => boolean = () => true) => f.findIndex((x) => re.test(x.name) && pred(x));
        const ia = idx(/^(at|addr|site|address|va)$/), ib = idx(/^(old|pro|orig|bytes|b|expect|prologue|want)$/, (x) => x.arr || x.ptr);
        const il = idx(/^len$/), it = idx(/^(target|callee)$/), is = idx(/^slot$/), ifn = idx(/^(fn|expect|want|orig)$/, (x) => !x.arr && !x.ptr);
        for (const el of s.init) {
            if (typeof el === "string") continue;
            const sc = { file: s.file, pos: s.pos }, val = (i: number) => (i >= 0 && typeof el[i] === "string" ? evalExpr(src, el[i] as string, sc) : null);
            const where = `${s.file}:${s.line}`, label = `${name}[${s.init.indexOf(el)}]`;
            if (ia >= 0) {
                const a = val(ia), t = val(it), bytes = ib >= 0 && el[ib] !== undefined ? arrBytes(src, el[ib], sc) : null, len = val(il);
                if (a === null || a === 0) continue;
                if (bytes && bytes.length && (il < 0 || (len ?? 0) > 0)) add(a, bytes, where, label, len);
                else if (t) call(a >>> 0, t >>> 0, where, label);
            } else if (is >= 0 && ifn >= 0) {
                const a = val(is), fn = val(ifn);
                if (a && fn) exp.push({ addr: a >>> 0, bytes: [fn & 255, (fn >> 8) & 255, (fn >> 16) & 255, (fn >>> 24) & 255], kind: "slot", target: fn >>> 0, where, name: label });
            }
        }
    }
    // InstallDetour(name, ADDR, BYTES, N, hook) / Matches(ADDR, BYTES, N)
    for (const [file, t] of src.texts) for (const m of t.matchAll(/\b(InstallDetour|Matches)\s*\(/g)) {
        if (/\b(bool|void\*?)\s+$/.test(t.slice(Math.max(0, m.index! - 12), m.index))) continue;  // the definition
        const a = splitArgs(t, m.index! + m[0].length), o = m[1] === "InstallDetour" ? 1 : 0;
        const where = `${file}:${t.slice(0, m.index).split("\n").length}`, ctx = { file, pos: m.index! };
        const cast = (e: string) => e.replace(/\(\s*(?:const\s+)?(?:void|u8|BYTE|char|DWORD)\s*\*?\s*\)|\(\s*uintptr_t\s*\)/g, "").trim();
        // a local alias of a table element (`const u8* p = (const u8*)ADDRS[i];`) stands for the element
        const alias = (e: string) => { const v = /^\w+$/.test(e) ? look(src, e, ctx) : undefined; return v && v.depth > 0 && typeof v.init === "string" && /^\w+\[\w+\]$/.test(cast(v.init)) ? cast(v.init) : e; };
        const A = alias(cast(a[o] ?? "")), B = alias((a[o + 1] ?? "").trim());
        if (/\]\s*(\.|->)/.test(A) || /\]\s*(\.|->)/.test(B)) continue;  // T[i].field: the table rule has it
        const ia = /^(\w+)\[(\w+)\]$/.exec(A), ib = /^(\w+)\[(\w+)\]$/.exec(B);
        const n = /^sizeof\s*\(/.test(a[o + 2] ?? "") ? null : evalExpr(src, a[o + 2] ?? "", ctx);
        const label = m[1] === "InstallDetour" ? (a[0] ?? "").replace(/^"|"$/g, "") : `Matches(${B})`;
        let ok = false;
        if (ia && ib) {
            const as = look(src, ia[1], ctx), bs = look(src, ib[1], ctx);
            if (as && bs && typeof as.init !== "string" && typeof bs.init !== "string")
                as.init.forEach((x, i) => { if (typeof x === "string" && bs.init[i] !== undefined) ok = add(evalExpr(src, x, ctx), arrBytes(src, (bs.init as Node[])[i], ctx), where, `${label}[${i}]`, n) || ok; });
        } else if (!ia && !ib) {
            const pa = /^\w+$/.test(A) ? paramArgs(src, file, m.index!, A) : undefined;
            // a parameter: the callers' arguments; none resolvable = unresolved, never a wrong address
            if (pa) ok = pa.length > 0 && pa.every((x) => add(evalExpr(src, cast(x.e), x.ctx), arrBytes(src, B, ctx), where, label, n));
            else ok = add(evalExpr(src, A, ctx), arrBytes(src, B, ctx), where, label, n);
        }
        if (!ok) unresolved.push(`${where} ${m[1]}(${A}, ${B})`);
    }
    // one row per (addr, bytes)
    const by = new Map<string, Expect>();
    for (const x of exp) { const k = `${x.addr}:${x.bytes.join(".")}`; const y = by.get(k); if (y) { if (!y.where.includes(x.where)) y.where += " " + x.where; } else by.set(k, { ...x }); }
    return { exp: [...by.values()].sort((a, b) => a.addr - b.addr), unresolved };
}

// docs/68 §2: addresses of rows whose kind patches (not reads/calls only)
export function patchedSites(md: string): number[] {
    const sec = md.split(/^## /m).find((s) => s.startsWith("2.")) ?? "";
    const out = new Set<number>();
    for (const l of sec.split(/\r?\n/)) {
        const c = l.split("|");
        if (c.length < 5 || !/0x/i.test(c[2])) continue;
        const kind = c[3].trim().toLowerCase();
        if (!kind || /^(reads? only|calls?( only)?|none|key|data|read)\b/.test(kind) && !/detour|slot|imm|site|patch|byte|swap/.test(kind)) continue;
        for (const m of c[2].matchAll(/0x([0-9a-f]{6,8})\b/gi)) out.add(parseInt(m[1], 16));
    }
    return [...out].sort((a, b) => a - b);
}

// ---- checking ----
export type Res = Expect & { new: number | null; kind2: string; found: string; mapKind: string };
const hx = (a: number | null | undefined) => (a === null || a === undefined ? "" : "0x" + a.toString(16).toUpperCase().padStart(8, "0"));
const hexb = (b: ArrayLike<number>) => Array.from(b, (x) => x.toString(16).toUpperCase().padStart(2, "0")).join(" ");

// expected-byte positions that hold a pointer or rel32 (they legitimately differ in another build)
function ptrMask(r: Pe, x: Expect): Uint8Array {
    const m = new Uint8Array(x.bytes.length), b = Uint8Array.from(x.bytes);
    if (x.kind !== "bytes") return m.fill(1, x.kind === "call" ? 1 : 0);
    const inImg = (v: number) => v >= r.base && v < r.base + r.img.length;
    for (let p = 0; p < b.length;) {
        const d = decode(b, p);
        if (!d) break;
        if (d.relSize === 4) m.fill(1, p + d.relOff, p + d.relOff + 4);
        if (d.dispSize === 4 && d.abs) m.fill(1, p + d.dispOff, p + d.dispOff + 4);
        if (d.immSize === 4 && inImg(b[p + d.immOff] | b[p + d.immOff + 1] << 8 | b[p + d.immOff + 2] << 16 | (b[p + d.immOff + 3] << 24 >>> 0))) m.fill(1, p + d.immOff, p + d.immOff + 4);
        p += d.len;
    }
    return m;
}

export function check(r: Pe, o: Pe, exp: Expect[]): Res[] {
    const same = r === o;
    const sites = [...new Set(exp.flatMap((x) => (x.target !== undefined ? [x.addr, x.target] : [x.addr])))].map((va) => ({ va, label: "", where: "" }));
    const map = new Map<number, { new: number | null; kind: string }>();
    if (same) for (const s of sites) map.set(s.va, { new: s.va, kind: "exact" });
    else for (const x of mapAll(r, o, sites).rows) map.set(x.ref, { new: x.new, kind: x.kind });
    return exp.map((x) => {
        let m = map.get(x.addr) ?? { new: null, kind: "missing" };
        if (x.kind === "slot" && m.new === null) {  // data slot: the one aligned dword outside code holding the mapped fn
            const fn = map.get(x.target!)?.new, hits: number[] = [];
            for (let p = 0x1000; fn != null && p + 4 <= o.img.length && hits.length < 2; p += 4)
                if (o.img.readUInt32LE(p) === fn && !o.exec.some(([a, b]) => p >= a && p < b)) hits.push(p);
            if (hits.length === 1) m = { new: o.base + hits[0], kind: "by-value" };
        }
        const res: Res = { ...x, new: m.new, kind2: "unmapped", found: "", mapKind: m.kind };
        if (m.new === null) return res;
        const rva = m.new - o.base;
        if (rva < 0 || rva + x.bytes.length > o.img.length) return res;
        const got = o.img.subarray(rva, rva + x.bytes.length);
        res.found = hexb(got);
        if (x.bytes.every((b, i) => b === got[i])) { res.kind2 = "ok"; return res; }
        if (x.kind === "call") {
            const t = map.get(x.target!)?.new;
            res.kind2 = got[0] === 0xe8 && t !== undefined && t !== null && m.new + 5 + got.readInt32LE(1) === t ? "ported" : "mismatch";
            return res;
        }
        const pm = ptrMask(r, x);
        res.kind2 = x.bytes.every((b, i) => pm[i] || b === got[i]) ? "ported" : "mismatch";
        return res;
    });
}

export function sourceTexts(): [string, string][] {
    return readdirSync(SRC).filter((f) => /\.(cpp|h)$/.test(f) && !/test|ilpatch-specs/.test(f)).sort().map((f) => [f, readFileSync(join(SRC, f), "utf8")]);
}

function report(res: Res[], unresolved: string[], unchecked: number[], patched: number, out: string, all: boolean, what: string) {
    mkdirSync(dirname(out), { recursive: true });
    writeFileSync(out, ["ref,new,kind,expect,mapKind,expected,found,name,where",
        ...res.map((x) => [hx(x.addr), hx(x.new), x.kind2, x.kind, x.mapKind, x.bytes.length > 16 ? hexb(x.bytes.slice(0, 16)) + " ..." : hexb(x.bytes), x.found.slice(0, 48), `"${x.name.replace(/"/g, "'")}"`, x.where].join(",")),
        ...unchecked.map((a) => [hx(a), "", "unchecked", "", "", "", "", "", "docs/68 §2"].join(",")),
        ...unresolved.map((u) => ["", "", "unresolved", "", "", "", "", `"${u.replace(/"/g, "'")}"`, ""].join(","))].join("\n") + "\n");
    const probs = res.filter((x) => all || x.kind2 !== "ok");
    for (const x of probs.slice(0, all ? Infinity : 40)) console.log(`${hx(x.addr)} ${x.kind2}${x.new !== null && x.new !== x.addr ? " at " + hx(x.new) : ""} (${x.mapKind}) want ${hexb(x.bytes.slice(0, 12))}${x.found ? " found " + x.found.slice(0, 35) : ""} ${x.name} ${x.where.split(" ")[0]}`);
    if (probs.length > 40 && !all) console.log(`... ${probs.length - 40} more problem rows in ${out}`);
    const n = (k: string) => res.filter((x) => x.kind2 === k).length;
    const ex = (k: string) => res.filter((x) => x.kind === k).length;
    console.log(`${what}: ${res.length} expectations (bytes ${ex("bytes")}, call ${ex("call")}, slot ${ex("slot")}) at ${new Set(res.map((x) => x.addr)).size} addresses: ok ${n("ok")}, ported ${n("ported")}, mismatch ${n("mismatch")}, unmapped ${n("unmapped")}; unchecked ${unchecked.length} of ${patched} docs/68 patched sites${unchecked.length ? " (" + unchecked.slice(0, 12).map(hx).join(" ") + (unchecked.length > 12 ? " ..." : "") + ")" : ""}; unresolved patch calls ${unresolved.length} -> ${out}`);
}

export function uncheckedSites(patched: number[], exp: Expect[]): number[] {
    return patched.filter((a) => !exp.some((x) => a >= x.addr && a < x.addr + Math.max(x.bytes.length, 5)));
}

function selfCheck(refPath?: string) {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`patch-bytes-check self-check FAILED: ${what}`); process.exit(1); } };
    let seed = 99;
    const code = Buffer.from(Array.from({ length: 0x2000 }, () => { const b = (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 16 & 0xff; return b === 0xe8 || b === 0xe9 ? 0x90 : b; }));
    const put = (off: number, b: number[]) => Buffer.from(b).copy(code, off);
    put(0x100, [0x55, 0x8b, 0xec, 0x83, 0xec, 0x10]);            // detour prologue A
    put(0x200, [0x56, 0x57, 0x8b, 0xf9]);                        // Site table entry
    put(0x300, [0xe8]); code.writeInt32LE(0x100 - 0x305, 0x301);  // call A from 0x300
    put(0x400, [0xa1, 0x00, 0x30, 0x40, 0x00]);                  // mov eax,[0x403000] (.data moves in the other exe)
    put(0x500, [0x8b, 0x41, 0x08]);                              // changed in the other exe
    put(0x600, [0x6a, 0x01]); put(0x700, [0x6a, 0x02]);          // indexed pair
    const src = readSources([["t.cpp", [
        "struct Site { DWORD addr; u8 expect[8]; size_t len; size_t immOff; };",
        "struct CallSite { DWORD site, target; void* hook; };",
        "struct SnIc { DWORD slot; DWORD fn; const char* name; };",
        "static const DWORD FN_A = 0x00401100, AT_LOAD = 0x00401400; // 0x00401999",
        "#define AT_CHG (FN_A + 0x400)",
        "static const u8 kPro[] = {0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10};",
        "static const u8 kLoad[] = \"\\xA1\\x00\\x30\\x40\\x00\";",
        "static const u8 kChg[3] = {0x8B, 0x41, 0x08};",
        "static const DWORD kIdx[2] = {0x00401600, 0x00401700};",
        "static const u8 kIdxPro[2][2] = {{0x6A, 0x01}, {0x6A, 0x02}};",
        "static const Site kS[] = {",
        "    {0x00401200, {0x56, 0x57, 0x8B, 0xF9}, 4, 0},",
        "};",
        "static const CallSite kC[1] = {{0x00401300, FN_A, NULL}};",
        "static const SnIc kSlot[] = {{0x00403010, FN_A, \"x\"}};",
        "void Go() {",
        "    void* t = InstallDetour(\"A\", FN_A, kPro, sizeof(kPro), (void*)Hook);",
        "    if (!Matches((const void*)(uintptr_t)AT_LOAD, kLoad, sizeof(kLoad)) || !Matches((void*)(uintptr_t)AT_CHG, kChg, 3)) return;",
        "    for (int i = 0; i < 2; i++) Matches((const void*)(uintptr_t)kIdx[i], kIdxPro[i], sizeof(kIdxPro[i]));",
        "    const u8 want[5] = {0xE8}; Matches(at, want, 5);",
        "}",
        "static const DWORD site = 0x00409999;",
        "static const u8 kPar[2] = {0x31, 0xC0};",
        "static bool Par(u8* site, int k) { return Matches(site, kPar, sizeof(kPar)); }",
        "void Go2() { Par((u8*)(uintptr_t)0x00401800, 1);",
        "}"].join("\n")]]);
    ok(evalExpr(src, "AT_CHG") === 0x401500 && evalExpr(src, "(DWORD)(FN_A - 8)") === 0x4010f8 && evalExpr(src, "sizeof(kPro)") === 6, "evalExpr");
    const { exp, unresolved } = extract(src);
    const at = (a: number) => exp.filter((x) => x.addr === a);
    ok(at(0x401100).length === 1 && at(0x401100)[0].bytes.length === 6, "InstallDetour prologue");
    ok(at(0x401200)[0]?.bytes.length === 4 && at(0x401300)[0]?.kind === "call" && at(0x403010)[0]?.kind === "slot", "Site / CallSite / slot tables");
    ok(at(0x401800).some((x) => x.bytes.length === 2) && at(0x409999).length === 0, "Matches on a parameter resolves from the caller, not a same-named global");
    ok(at(0x401400)[0]?.bytes.length === 5 && at(0x401500)[0]?.bytes.length === 3 && at(0x401600).length === 1 && at(0x401700)[0]?.bytes[1] === 2, "Matches: string bytes, #define, indexed");
    ok(unresolved.length === 1 && /want/.test(unresolved[0]), `one unresolved (local want): ${unresolved.join("; ")}`);
    const data = (shift: number) => { const d = Buffer.alloc(0x200); d.writeUInt32LE(0x401100 + shift, 0x10); return d; };
    // makePe puts .data after .text: ref .data at 0x403000; other has 0x1000 more code, 0x40 bytes inserted at 0
    const r = parsePe(makePe(Buffer.concat([code]), []));
    r.img.set(data(0), 0x3000);
    const oc = Buffer.concat([Buffer.alloc(0x40, 0xcc), code, Buffer.alloc(0xfc0, 0xcc)]);
    oc.writeUInt32LE(0x404000, 0x40 + 0x401); oc[0x40 + 0x502] = 0x0c;
    const o = parsePe(makePe(oc, []));
    o.img.set(data(0x40), 0x4000);
    const id = check(r, r, exp.filter((x) => x.addr !== 0x401800));  // 0x401800: the parameter fixture, no bytes in the fake image
    ok(id.every((x) => x.kind2 === "ok"), `identity all ok: ${id.filter((x) => x.kind2 !== "ok").map((x) => hx(x.addr)).join(" ")}`);
    const res = check(r, o, exp), k = (a: number) => res.find((x) => x.addr === a)!.kind2;
    ok(k(0x401100) === "ok" && k(0x401200) === "ok" && k(0x401300) === "ok", "moved code: same bytes");
    ok(k(0x401400) === "ported" && k(0x401500) === "mismatch", "pointer operand = ported, changed displacement = mismatch");
    const sl = res.find((x) => x.addr === 0x403010)!;
    ok(sl.kind2 === "ported" && sl.new === 0x404010 && sl.mapKind === "by-value", "data slot found by its mapped value");
    ok(patchedSites("## 2. Ours\n| M | A | K | W |\n|---|---|---|---|\n| x | `0x401100`, `0x401234` | detour | a |\n| y | `0x401300` | reads only | b |\n## 3. x\n| z | `0x401999` | detour | c |").join() === [0x401100, 0x401234].join(), "docs/68 §2 patched sites");
    ok(uncheckedSites([0x401100, 0x401103, 0x401234], exp).join() === String(0x401234), "unchecked = not covered");
    console.log("patch-bytes-check self-check: synthetic ok");
    const real = refPath ?? join(repo, "game-copy", "bin", "TS3W.exe");
    if (!existsSync(real)) { console.log("patch-bytes-check self-check: real exe absent, skipped"); return; }
    const pe = parsePe(readFileSync(real)), all = extract(readSources(sourceTexts())).exp;
    const bad = check(pe, pe, all).filter((x) => x.kind2 !== "ok");
    ok(bad.length === 0, `Steam exe: ${bad.length} of ${all.length} expectations differ (${bad.slice(0, 5).map((x) => hx(x.addr)).join(" ")})`);
    console.log(`patch-bytes-check self-check: Steam exe ${all.length} expectations ok`);
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { ref: { type: "string" }, other: { type: "string" }, out: { type: "string" }, all: { type: "boolean" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck(v.ref);
    else {
        const refPath = v.ref ?? join(repo, "game-copy", "bin", "TS3W.exe");
        const r = parsePe(readFileSync(refPath)), o = v.other ? parsePe(readFileSync(v.other)) : r;
        const { exp, unresolved } = extract(readSources(sourceTexts()));
        const patched = patchedSites(readFileSync(join(repo, "docs", "68-hook-conflict-map.md"), "utf8"));
        const out = v.out ?? join(repo, "out", "patch-bytes-check", basename(v.other ?? refPath).replace(/\.\w+$/, "") + ".csv");
        report(check(r, o, exp), unresolved, uncheckedSites(patched, exp), patched.length, out, !!v.all, v.other ? `${basename(v.other)} vs Steam` : "Steam exe (self-validation)");
    }
}
