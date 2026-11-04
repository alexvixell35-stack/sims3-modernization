// One command for a port to another exe build (docs/68 §7.5): map -> expected bytes -> TS3A table, one screen out.
//
//   node tools/port-run.ts --exe <decrypted TS3W.exe|TS3.exe> [--ref <Steam exe>] [--allow-inferred] [--build] [--ghidra-names]
//   node tools/port-run.ts --zip <Sims3PortReport-xxxxxxxx.zip> [same options]
//   node tools/port-run.ts --self-check
//
// Stages: map (exe-addr-map.ts --other, or port-report.ts for a player zip), patch-bytes-check against that exe (or, for a
// zip, the map kind of every expectation: no bytes to compare), addr-codemod gen (strict unless --allow-inferred).
// Prints one summary line per stage, writes out/port/<id>/problems.csv (priority: 1 patch sites = crash risk,
// 2 hooks and other code, 3 reads/data; one row per address, stages merged) and ends with `AI needed: none` or
// `AI needed: N rows (see <file>)`. A row with a candidate address (near, hint, fingerprint guess) is ai=no: it needs
// --allow-inferred plus the bytes check, not a manual hunt. Everything under out/port/ is derived from EA bytes: never commit.
// --build prints (never runs) the TS3A_TABLE build and the smoke queue command; --ghidra-names writes
// <id>/ghidra-names.csv for tools/ghidra/ImportSymbols.java (tools/port-ghidra-names.ts).
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { basename, dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { csvRow } from "./hook-sites.ts";
import { parsePe } from "./exe-addr-map.ts";
import { check, extract, patchedSites, readSources, sourceTexts, type Expect } from "./patch-bytes-check.ts";
import { dataRanges, gen, generated, scanText, sourceFiles } from "./addr-codemod.ts";
import { readReport } from "./port-report.ts";
import { mapExtra, portNames, readCands, readMap, steamNames, type MapRow } from "./port-ghidra-names.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const SRC = join(repo, "native", "ts3native");
const hx = (a: number | null | undefined) => (a === null || a === undefined ? "" : "0x" + a.toString(16).toUpperCase().padStart(8, "0"));
const sha = (b: Buffer) => createHash("sha256").update(b).digest("hex");
const rel = (p: string) => relative(repo, p).replace(/\\/g, "/");
const GOOD = new Set(["exact", "masked", "ref"]);

export type PatchRow = { addr: number; new: number | null; kind2: string; mapKind: string; name: string; where: string };
export type Problem = { prio: number; stage: string; addr: number | null; label: string; kind: string; cand: string; ai: boolean; where: string };

// best map row per Steam address (a site can be both a site row and a ref row)
export function bestMap(rows: MapRow[]): Map<number, MapRow> {
    const rank = (k: string) => (k === "exact" ? 4 : k === "masked" ? 3 : k === "ref" ? 2 : k === "near" || k === "offset-changed" ? 1 : 0);
    const out = new Map<number, MapRow>();
    for (const m of rows) { const b = out.get(m.ref); if (!b || rank(m.kind) > rank(b.kind) || (rank(m.kind) === rank(b.kind) && b.new === null && m.new !== null)) out.set(m.ref, m); }
    return out;
}

// zip: no bytes, so an expectation is as good as its site's map row (exact = every signature tile around it matched)
export function patchFromMap(exp: Expect[], best: Map<number, MapRow>): PatchRow[] {
    return exp.map((x) => {
        const m = best.get(x.addr), t = x.target === undefined ? null : best.get(x.target);
        const kind2 = !m || m.new === null ? "unmapped" : x.kind === "call" && (!t || t.new === null) ? "target-unmapped"
            : m.kind === "exact" && (!t || t.kind === "exact" || t.kind === "masked") ? "ok" : GOOD.has(m.kind) ? "unverified" : "inferred";
        return { addr: x.addr, new: m?.new ?? null, kind2, mapKind: m?.kind ?? "missing", name: x.name, where: x.where };
    });
}

export type Inputs = {
    map: MapRow[]; patch: PatchRow[]; genErrors: string[]; managed: { module: string; method: string; status: string }[];
    patchAddrs: Set<number>; cands: Map<number, { guess: number; score: number }>; names: Map<number, string>; whereBy: Map<number, string>;
};

export function problems(inp: Inputs): Problem[] {
    const best = bestMap(inp.map), by = new Map<number, Problem>(), extra: Problem[] = [];
    const add = (stage: string, addr: number, kind: string, hardAi: boolean, label = "", where = "") => {
        const m = best.get(addr), c = inp.cands.get(addr);
        const cand = m && m.new !== null ? `${hx(m.new)} (${m.kind})` : c ? `${hx(c.guess)} score ${c.score}` : m?.hint ? `hint ${hx(m.hint)}` : "";
        const p = by.get(addr) ?? { prio: inp.patchAddrs.has(addr) ? 1 : addr < 0x00f95000 ? 2 : 3, stage: "", addr, label: "", kind: "", cand, ai: false, where: "" };
        p.stage = p.stage ? `${p.stage};${stage}` : stage;
        p.kind = p.kind ? `${p.kind};${kind}` : kind;
        p.label ||= label || m?.label || inp.names.get(addr) || "";
        p.where ||= inp.whereBy.get(addr) || where || m?.where || "";
        p.ai = p.ai || hardAi || !cand;
        by.set(addr, p);
    };
    for (const m of best.values()) if (!GOOD.has(m.kind) && m.kind !== "not-code") add("map", m.ref, m.kind, m.kind === "ref-conflict");
    for (const x of inp.patch) if (x.kind2 !== "ok") add("patch", x.addr, `${x.kind2} (${x.mapKind})`, x.kind2 === "mismatch", x.name, x.where);
    for (const e of inp.genErrors) { const a = e.match(/^0x([0-9A-F]+) (.*)$/i); if (a) add("gen", parseInt(a[1], 16), a[2], false); }
    for (const g of inp.managed) extra.push({ prio: 2, stage: "managed", addr: null, label: `${g.module} ${g.method}`, kind: g.status, cand: "", ai: true, where: "mods/ts3fixes/src" });
    return [...by.values(), ...extra].sort((a, b) => a.prio - b.prio || Number(b.ai) - Number(a.ai) || (a.addr ?? Infinity) - (b.addr ?? Infinity));
}

export function problemsCsv(ps: Problem[]): string {
    const q = (s: string) => `"${s.replace(/"/g, '""')}"`;
    return ["prio,stage,address,label,kind,candidates,ai,where", ...ps.map((p) => [p.prio, p.stage, hx(p.addr), q(p.label), q(p.kind), q(p.cand), p.ai ? "yes" : "no", q(p.where)].join(","))].join("\n") + "\n";
}

export const aiLine = (ps: Problem[], file: string) => { const n = ps.filter((p) => p.ai).length; return n ? `AI needed: ${n} rows (see ${file})` : "AI needed: none"; };

const node = (args: string[]) => {
    try { return execFileSync(process.execPath, args, { cwd: repo, encoding: "utf8", maxBuffer: 64 << 20, stdio: ["ignore", "pipe", "pipe"] }); }
    catch (e: any) { console.error(`${args[0]} failed (exit ${e.status}):\n${String(e.stdout ?? "").split("\n").slice(-5).join("\n")}${e.stderr ?? ""}`); process.exit(1); }
};

function run(v: { zip?: string; exe?: string; ref?: string; asm?: string; "allow-inferred"?: boolean; build?: boolean; "ghidra-names"?: boolean; "min-score"?: string }) {
    const refPath = v.ref ?? join(repo, "game-copy", "bin", "TS3W.exe");
    if (!existsSync(refPath)) { console.error(`Steam exe not found: ${refPath} (--ref)`); process.exit(2); }
    const refBuf = readFileSync(refPath), input = (v.zip ?? v.exe)!;
    const otherBuf = v.exe ? readFileSync(v.exe) : null;
    const id = v.exe ? `${basename(v.exe).replace(/\.\w+$/, "")}-${sha(otherBuf!).slice(0, 8)}` : basename(v.zip!).replace(/\.\w+$/, "");
    const dir = join(repo, "out", "port", id), mapCsv = join(dir, "map.csv");
    mkdirSync(dir, { recursive: true });
    const otherSha = otherBuf ? sha(otherBuf) : readReport(v.zip!).slice(0, 4000).match(/sha256=([0-9a-f]{64})/)?.[1];
    const same = otherSha === sha(refBuf);
    console.log(`port-run ${id}: ${v.exe ? "exe" : "player zip"} ${input}${same ? " (same sha256 as the Steam exe: identity run)" : ""}`);

    // 1. map
    const out = v.exe ? node(["tools/exe-addr-map.ts", "--other", v.exe, "--ref", refPath, "--out", mapCsv])
        : node(["tools/port-report.ts", v.zip!, "--ref", refPath, "--out", mapCsv, "--questions", join(dir, "questions.json"), ...(v.asm ? ["--asm", v.asm] : [])]);
    console.log(`map: ${out.split(/\r?\n/).find((l) => /^\d+ addresses:/.test(l)) ?? "(no summary line)"}`);

    // sources: expectations and codemod literals
    // probe-only expectations from generated headers (vcallsites*.h, /DTS3N_VCSITES*) are out of scope, as in addr-codemod
    const texts = sourceTexts(), gens = new Set(texts.filter(([, t]) => generated(t)).map(([f]) => f));
    const all = extract(readSources(texts)).exp, exp = all.filter((x) => !x.where.split(" ").every((w) => gens.has(w.replace(/:\d+$/, ""))));
    const data = dataRanges(refPath), scan = sourceFiles().flatMap((f) => { const t = readFileSync(join(SRC, f), "utf8"); return generated(t) ? [] : scanText(t, f, data); });
    const needed = scan.filter((r) => r.kind === "wrap" || r.kind === "wrapped").map((r) => r.addr);
    const whereBy = new Map<number, string>();
    for (const r of scan) whereBy.set(r.addr, whereBy.has(r.addr) ? whereBy.get(r.addr)!.replace(/( \(\+(\d+)\))?$/, (_, __, n) => ` (+${Number(n ?? 0) + 1})`) : `native/ts3native/${r.file}:${r.line}`);

    // identical bytes: addresses the map cannot reach (data nobody's matched window reads) are the same address
    let mapText = readFileSync(mapCsv, "utf8");
    if (same) {
        const have = new Set(readMap(mapText).filter((m) => m.new !== null).map((m) => m.ref));
        const add = [...new Set([...needed, ...exp.flatMap((x) => (x.target === undefined ? [x.addr] : [x.addr, x.target]))])].filter((a) => !have.has(a));
        mapText = mapText.trimEnd() + "\n" + add.map((a) => `${hx(a)},${hx(a)},exact,1,0,,,,,identity (same sha256)`).join("\n") + "\n";
        writeFileSync(mapCsv, mapText);
        console.log(`identity: +${add.length} addresses outside the map taken as the same address`);
    }
    const map = readMap(mapText), best = bestMap(map);

    // 2. expected original bytes
    let patch: PatchRow[];
    if (v.exe) {
        const r = parsePe(refBuf), o = parsePe(otherBuf!);
        patch = check(r, o, exp).map((x) => ({ addr: x.addr, new: x.new, kind2: x.kind2, mapKind: x.mapKind, name: x.name, where: x.where }));
        for (const x of patch) {  // its own mapping missed it: try the map row (ref rows for data)
            const m = best.get(x.addr), e = exp.find((y) => y.addr === x.addr && y.name === x.name)!;
            if (x.kind2 !== "unmapped" || !m || m.new === null) continue;
            const rva = m.new - o.base, got = rva >= 0 && rva + e.bytes.length <= o.img.length ? o.img.subarray(rva, rva + e.bytes.length) : null;
            x.new = m.new; x.mapKind = `map ${m.kind}`; x.kind2 = got && e.bytes.every((b, i) => b === got[i]) ? "ok" : "mismatch";
        }
    } else patch = patchFromMap(exp, best);
    const pc = (k: string) => patch.filter((x) => x.kind2 === k).length;
    const kinds = [...new Set(patch.map((x) => x.kind2))].filter((k) => k !== "ok");
    console.log(`patch: ${patch.length} expectations at ${new Set(patch.map((x) => x.addr)).size} addresses: ok ${pc("ok")}${kinds.map((k) => `, ${k} ${pc(k)}`).join("")}; probe-only (generated headers) skipped ${all.length - exp.length}${v.zip ? " (zip: from map kinds, no bytes)" : ""}`);

    // 3. TS3A table
    const g = gen(mapText, needed, !!v["allow-inferred"], basename(mapCsv)), table = join(dir, "ts3addr-table.h");
    if (!g.errors.length) writeFileSync(table, g.header);
    const notApplied = scan.some((r) => r.kind === "wrap");
    console.log(`gen: ${new Set(needed).size} addresses, ${g.inferred} inferred, ${g.errors.length} without a usable row${v["allow-inferred"] ? "" : " (strict)"}${g.errors.length ? "" : ` -> ${rel(table)}`}`);

    // problems
    let managed: Inputs["managed"] = [];
    const mf = join(dir, "map.managed.csv");
    if (v.zip && existsSync(mf)) managed = readFileSync(mf, "utf8").trim().split(/\r?\n/).slice(1).map(csvRow)
        .filter((c) => c[3] === "yes" && c[2] !== "hash-changed").map((c) => ({ module: c[0], method: c[1], status: c[2] }));
    const cf = join(dir, "map.candidates.csv"), cands = existsSync(cf) ? readCands(readFileSync(cf, "utf8")) : new Map();
    const names = steamNames(map);
    const ps = problems({ map, patch, genErrors: g.errors, managed, cands, whereBy,
        patchAddrs: new Set([...exp.map((x) => x.addr), ...patchedSites(readFileSync(join(repo, "docs", "68-hook-conflict-map.md"), "utf8"))]),
        names: new Map(names.map((n) => [n.addr, n.name])) });
    const pfile = join(dir, "problems.csv");
    writeFileSync(pfile, problemsCsv(ps));
    const pr = (n: number) => ps.filter((p) => p.prio === n).length;
    console.log(`problems: ${ps.length} rows (patch sites ${pr(1)}, hooks/code ${pr(2)}, reads/data ${pr(3)}; ai=yes ${ps.filter((p) => p.ai).length}) -> ${rel(pfile)}`);

    if (v["ghidra-names"]) {
        let gmap = map;
        if (v.exe) gmap = map.concat(mapExtra(parsePe(refBuf), parsePe(otherBuf!), map, names));
        const r = portNames(gmap, names, cands, Number(v["min-score"] ?? 6)), gf = join(dir, "ghidra-names.csv");
        writeFileSync(gf, r.csv);
        console.log(`ghidra names: ${r.sure} ported, ${r.guess} _guess, ${r.skipped} not placed of ${names.length} -> ${rel(gf)}`);
        console.log(`  apply (not run): analyzeHeadless <projectDir> <project> -import <exe> -scriptPath tools/ghidra -postScript ImportSymbols.java ${rel(gf)} force`);
    }

    if (v.build) {
        const outDir = `out-port-${id}`, asi = `native/ts3native/${outDir}/ts3native.asi`;
        console.log("build (printed, not run):");
        if (notApplied) console.log("  sources not on TS3A yet: run addr-codemod --apply first (post-Mono refactor)");
        if (g.errors.length) console.log(`  no table: gen has ${g.errors.length} addresses without a usable row (problems.csv stage gen${v["allow-inferred"] ? "" : "; or --allow-inferred"})`);
        console.log(`  copy ${rel(table).replace(/\//g, "\\")} native\\ts3native\\out\\ts3addr-table.h`);
        console.log(`  cmd /c "set CL=/DTS3A_TABLE&& native\\ts3native\\build.cmd ${outDir}"`);
        if (v.exe) console.log(`  node tools/patch-bytes-check.ts --other "${v.exe}"`);
        const rc = join(repo, "tools", "harness", "queues", "release-check.json");
        if (existsSync(rc)) {
            const job = JSON.parse(readFileSync(rc, "utf8")).jobs?.find((j: any) => /smoke/.test(j.id));
            if (job) {
                const q = join(dir, "port-smoke.json");
                writeFileSync(q, JSON.stringify({ note: `port smoke for ${id}: release-check ${job.id} with the TS3A_TABLE build (tools/port-run.ts --build)`, jobs: [{ ...job, id: `port-${id}-smoke`, asi }] }, null, 2) + "\n");
                console.log(`  node tools/harness/queue.ts ${rel(q)}    (on a machine with that exe installed)`);
            }
        }
    }
    console.log(aiLine(ps, rel(pfile)));
}

function selfCheck() {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`port-run self-check FAILED: ${what}`); process.exit(1); } };
    const map = readMap(["ref,new,kind,count,delta,window,funcOff,hint,label,where",
        "0x00401000,0x00401010,exact,1,16,32,+0x0,,,a.cpp:1", "0x00402000,,missing,0,,,,0x00402020,HookB,b.cpp:2",
        "0x00403000,,missing,0,,,,,,", "0x00404000,0x00404040,near,2,64,,,,,", "0x01150000,,not-code,0,,,,,,", "0x01160000,0x01160010,ref-conflict,3,16,,,,,"].join("\n"));
    const best = bestMap(map);
    const exp: Expect[] = [{ addr: 0x401000, bytes: [0x55], kind: "bytes", where: "a.cpp:1", name: "A" }, { addr: 0x404000, bytes: [0x55], kind: "bytes", where: "d.cpp:4", name: "D" },
        { addr: 0x403000, bytes: [0xe8, 0, 0, 0, 0], kind: "call", target: 0x401000, where: "c.cpp:3", name: "C" }];
    const patch = patchFromMap(exp, best);
    ok(patch.map((x) => x.kind2).join() === "ok,inferred,unmapped", `patchFromMap ${patch.map((x) => x.kind2)}`);
    const ps = problems({ map, patch, genErrors: ["0x01150000 not in the map", "0x00404000 kind near (needs --allow-inferred)"], managed: [{ module: "Sims3GameplaySystems", method: "X::Y", status: "removed" }],
        patchAddrs: new Set([0x403000, 0x404000]), cands: new Map([[0x403000, { guess: 0x403030, score: 9 }]]), names: new Map([[0x403000, "Fn_C"]]), whereBy: new Map([[0x402000, "native/ts3native/b.cpp:2"]]) });
    const at = (a: number) => ps.find((p) => p.addr === a)!;
    ok(ps.length === 6, `rows ${ps.length}`);
    ok(ps[0].prio === 1 && ps[1].prio === 1 && ps[2].prio === 2, "patch sites first");
    ok(at(0x404000).stage === "map;patch;gen" && !at(0x404000).ai && at(0x404000).cand.startsWith("0x00404040"), "merged near row, candidate, ai=no");
    ok(at(0x403000).cand === "0x00403030 score 9" && at(0x403000).label === "Fn_C" && !at(0x403000).ai, "fingerprint candidate + name");
    ok(at(0x402000).cand === "hint 0x00402020" && at(0x402000).where === "native/ts3native/b.cpp:2", "hint + source line");
    ok(at(0x1150000).prio === 3 && at(0x1150000).ai && at(0x1160000).ai, "data rows last, no candidate / conflict = ai");
    ok(ps[ps.length - 1].addr === 0x1160000 && ps.some((p) => p.stage === "managed" && p.ai), "managed row");
    const csv = problemsCsv(ps).split("\n");
    ok(csv[0] === "prio,stage,address,label,kind,candidates,ai,where" && csv.length === ps.length + 2, "csv shape");
    ok(aiLine(ps, "x.csv") === "AI needed: 3 rows (see x.csv)" && aiLine([], "x") === "AI needed: none", `ai line ${aiLine(ps, "x.csv")}`);
    console.log("port-run self-check: ok");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { zip: { type: "string" }, exe: { type: "string" }, ref: { type: "string" }, asm: { type: "string" }, "allow-inferred": { type: "boolean" },
        build: { type: "boolean" }, "ghidra-names": { type: "boolean" }, "min-score": { type: "string" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck();
    else if (!v.zip === !v.exe) { console.error("usage: node tools/port-run.ts (--exe <exe> | --zip <player report>) [--ref <Steam exe>] [--asm <dir>] [--allow-inferred] [--build] [--ghidra-names] [--min-score 6] | --self-check"); process.exit(2); }
    else run(v);
}
