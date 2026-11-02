// Ghidra name transfer for a ported exe (docs/68 §7.5): turns a port map (exe-addr-map / port-report CSV, plus the
// fingerprint candidates above a score) into the CSV tools/ghidra/ImportSymbols.java reads (address,name,type,comment),
// with the Steam 1.67 names from tools/ghidra/symbols/ts3w-1.67.symbols.csv, docs/data/ic-safety.csv (ICs),
// docs/data/mint-opcodes.csv (mint handlers) and our sources' hook-site labels (the map's label column).
// Inferred rows (near, offset-changed, fingerprint guesses) get the suffix `_guess`; exact/masked/ref rows keep the name.
//
//   node tools/port-ghidra-names.ts --map out/port/<id>/map.csv [--candidates <csv>] [--min-score 6] [--other <exe>] [--ref <Steam exe>] [--out file]
//   node tools/port-ghidra-names.ts --self-check
//
// --other (the decrypted exe): every named Steam address the map lacks is mapped too (exe-addr-map mapAll), so most of
// the 3.8k names carry over, not only our ~900 sites. Output is derived from EA addresses: keep it under out/ (gitignored).
// Apply (writes the Ghidra project; not run by this tool):
//   analyzeHeadless <projectDir> <project> -import <exe> -scriptPath tools/ghidra -postScript ImportSymbols.java <csv> force
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { csvRow } from "./hook-sites.ts";
import { mapAll, parsePe } from "./exe-addr-map.ts";
import type { Pe } from "./exe-port-diff.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const hx = (a: number) => "0x" + a.toString(16).toUpperCase().padStart(8, "0");
const num = (s: string | undefined) => (s && /^0x[0-9a-f]+$/i.test(s.trim()) ? parseInt(s, 16) : null);

export type MapRow = { ref: number; new: number | null; kind: string; hint: number | null; label: string; where: string };
export type Cand = { guess: number; score: number };
export type Name = { addr: number; name: string; type: string; comment: string };

export function readMap(text: string): MapRow[] {
    const [h, ...ls] = text.trim().split(/\r?\n/), head = csvRow(h), c = (r: string[], k: string) => r[head.indexOf(k)] ?? "";
    return ls.map(csvRow).filter((r) => num(c(r, "ref")) !== null)
        .map((r) => ({ ref: num(c(r, "ref"))!, new: num(c(r, "new")), kind: c(r, "kind"), hint: num(c(r, "hint")), label: c(r, "label"), where: c(r, "where") }));
}

// port-report <base>.candidates.csv: ref,fn,cand,score,second,guess,why
export function readCands(text: string): Map<number, Cand> {
    const out = new Map<number, Cand>();
    for (const r of text.trim().split(/\r?\n/).slice(1).map(csvRow)) {
        const ref = num(r[0]), guess = num(r[5]);
        if (ref !== null && guess !== null) out.set(ref, { guess, score: Number(r[3]) || 0 });
    }
    return out;
}

const safe = (s: string) => s.replace(/[^A-Za-z0-9_.]/g, "_").replace(/^(\d)/, "_$1");

// Steam names, first source wins per address: symbol file, ICs, mint handlers, hook-site labels of the map
export function steamNames(map: MapRow[] = [], dir = repo): Name[] {
    const by = new Map<number, Name>();
    const add = (n: Name) => { if (!by.has(n.addr) && n.name) by.set(n.addr, n); };
    const rows = (f: string) => (existsSync(f) ? readFileSync(f, "utf8").trim().split(/\r?\n/).map(csvRow) : []);
    const [sh, ...sym] = rows(join(dir, "tools", "ghidra", "symbols", "ts3w-1.67.symbols.csv"));
    if (sh) for (const r of sym) { const a = num(r[0]); if (a !== null && r[2] !== "type") add({ addr: a, name: r[1], type: r[2], comment: r[3] ?? "" }); }
    const [ih, ...ics] = rows(join(dir, "docs", "data", "ic-safety.csv"));
    if (ih) for (const r of ics) { const a = num(r[ih.indexOf("address")]); if (a !== null) add({ addr: a, name: "IC_" + safe(r[ih.indexOf("name")]), type: "icall", comment: `docs/data/ic-safety.csv ${r[0]}` }); }
    const [mh, ...ops] = rows(join(dir, "docs", "data", "mint-opcodes.csv"));
    if (mh) for (const r of ops) { const a = num(r[mh.indexOf("handler")]); if (a !== null) add({ addr: a, name: "mint_" + safe(r[mh.indexOf("name")]), type: "label", comment: `docs/data/mint-opcodes.csv ${r[0]}` }); }
    for (const m of map) if (m.label) add({ addr: m.ref, name: "ts3n_" + safe(m.label), type: "label", comment: `our source ${m.where}` });
    return [...by.values()].sort((a, b) => a.addr - b.addr);
}

const SURE = new Set(["exact", "masked", "ref"]), GUESS = new Set(["near", "offset-changed"]);

export function portNames(map: MapRow[], names: Name[], cands = new Map<number, Cand>(), minScore = 6) {
    const best = new Map<number, MapRow>();
    const rank = (k: string) => (SURE.has(k) ? 2 : GUESS.has(k) ? 1 : 0);
    for (const m of map) { const b = best.get(m.ref); if (!b || rank(m.kind) > rank(b.kind)) best.set(m.ref, m); }
    let sure = 0, guess = 0, skipped = 0;
    const used = new Set<number>(), out = ["address,name,type,comment"];
    const q = (s: string) => `"${s.replace(/"/g, '""')}"`;
    for (const n of names) {
        const m = best.get(n.addr), c = cands.get(n.addr);
        let at: number | null = null, inferred = false, how = "";
        if (m && m.new !== null && SURE.has(m.kind)) { at = m.new; how = m.kind; }
        else if (m && m.new !== null && GUESS.has(m.kind)) { at = m.new; inferred = true; how = m.kind; }
        else if (c && c.score >= minScore) { at = c.guess; inferred = true; how = `fingerprint score ${c.score}`; }
        if (at === null || used.has(at)) { skipped++; continue; }
        used.add(at);
        if (inferred) guess++; else sure++;
        const tag = inferred ? "[unverified: ported, inferred]" : "[unverified: ported]";
        out.push([hx(at), n.name + (inferred ? "_guess" : ""), n.type, q(`${tag} Steam 1.67 ${hx(n.addr)} via ${how}; ${n.comment.replace(/^\[[^\]]*\]\s*/, "")}`)].join(","));
    }
    return { csv: out.join("\n") + "\n", sure, guess, skipped };
}

// names whose Steam address the map lacks, mapped with exe-addr-map's own matcher (needs the other exe's bytes)
export function mapExtra(r: Pe, o: Pe, map: MapRow[], names: Name[]): MapRow[] {
    const have = new Set(map.map((m) => m.ref));
    const { rows } = mapAll(r, o, names.filter((n) => !have.has(n.addr)).map((n) => ({ va: n.addr, label: "", where: "" })));
    return rows.map((x) => ({ ref: x.ref, new: x.new, kind: x.kind, hint: x.hint ?? null, label: "", where: "" }));
}

function selfCheck() {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`port-ghidra-names self-check FAILED: ${what}`); process.exit(1); } };
    const map = readMap(["ref,new,kind,count,delta,window,funcOff,hint,label,where",
        "0x00401000,0x00402000,exact,1,4096,32,+0x0,,,", "0x00401100,0x00402110,near,2,,,,,\"Hook \"\"A\"\"\",x.cpp:3",
        "0x00401200,,missing,0,,,,0x00402200,,", "0x00401300,,missing,0,,,,,,", "0x01000000,0x01000010,ref,5,16,,,,,"].join("\n"));
    ok(map.length === 5 && map[1].label === 'Hook "A"' && map[2].hint === 0x402200, "readMap");
    const cands = readCands("ref,fn,cand,score,second,guess,why\n0x00401200,0x401200,0x402200,9,2,0x00402200,score\n0x00401300,0x401300,0x402300,4,3,0x00402300,weak\n");
    const names: Name[] = [{ addr: 0x401000, name: "Fn_A", type: "function", comment: "[verified: x] a" }, { addr: 0x401100, name: "Fn_B", type: "function", comment: "" },
        { addr: 0x401200, name: "Fn_C", type: "function", comment: "" }, { addr: 0x401300, name: "Fn_D", type: "function", comment: "" }, { addr: 0x1000000, name: "g_E", type: "data", comment: "" }];
    const r = portNames(map, names, cands, 6), lines = r.csv.trim().split("\n");
    ok(r.sure === 2 && r.guess === 2 && r.skipped === 1, `counts ${r.sure}/${r.guess}/${r.skipped}`);
    ok(lines[1].startsWith("0x00402000,Fn_A,function,") && !lines[1].includes("[verified"), "exact row keeps name, drops verified tag");
    ok(lines[2].startsWith("0x00402110,Fn_B_guess,") && lines[3].startsWith("0x00402200,Fn_C_guess,"), "near + fingerprint rows get _guess");
    ok(lines[4].startsWith("0x01000010,g_E,data,"), "ref row");
    ok(steamNames(map, join(repo, "no-such-dir"))[0]?.name === "ts3n_Hook__A_", "hook label fallback name");
    console.log("port-ghidra-names self-check: ok");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { map: { type: "string" }, candidates: { type: "string" }, "min-score": { type: "string" }, other: { type: "string" }, ref: { type: "string" }, out: { type: "string" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck();
    else if (!v.map) { console.error("usage: node tools/port-ghidra-names.ts --map <map.csv> [--candidates <csv>] [--min-score 6] [--other <exe>] [--ref <Steam exe>] [--out file] | --self-check"); process.exit(2); }
    else {
        let map = readMap(readFileSync(v.map, "utf8"));
        const names = steamNames(map);
        if (v.other) map = map.concat(mapExtra(parsePe(readFileSync(v.ref ?? join(repo, "game-copy", "bin", "TS3W.exe"))), parsePe(readFileSync(v.other)), map, names));
        const cands = v.candidates ? readCands(readFileSync(v.candidates, "utf8")) : new Map<number, Cand>();
        const r = portNames(map, names, cands, Number(v["min-score"] ?? 6));
        const out = v.out ?? v.map.replace(/\.csv$/, "") + ".ghidra-names.csv";
        mkdirSync(dirname(out), { recursive: true });
        writeFileSync(out, r.csv);
        console.log(`ghidra names: ${names.length} Steam names, ${r.sure} ported, ${r.guess} _guess, ${r.skipped} not placed -> ${out}`);
        console.log(`apply: analyzeHeadless <projectDir> <project> -import <exe> -scriptPath tools/ghidra -postScript ImportSymbols.java ${out} force`);
    }
}
