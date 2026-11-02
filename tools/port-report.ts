// Turn a player's port report (tools/port-probe/Sims3PortProbe.ps1, docs/68 §7.4) into the tools/exe-addr-map.ts CSV,
// so `addr-codemod.ts gen --map` and the other port tools read it as if exe-addr-map had run on the other exe.
// Deterministic, offline; prints one summary line, the problem rows and `AI needed: ...` last.
//
//   node tools/port-report.ts <Sims3PortReport-xxxxxxxx.zip|.txt> [--ref <Steam TS3W.exe>] [--sigs <port-sigs.txt>] [--asm <dir>] [--out file.csv] [--questions q.json] [--all]
//   node tools/port-report.ts --self-check [--ref <Steam TS3W.exe>] [--ps]    --ps: also run the probe's C# matcher on the
//                                                                              Steam exe (Windows) and compare line by line
//
// Steam values (field values, call targets, function offsets) come from the local Steam exe; the report holds only the
// other exe's addresses and numbers. Kinds as exe-addr-map: exact (all tiles and pointer operands equal), masked,
// offset-changed (unique; displacement/immediate values differ inside mismatched tiles: <out>.offsets.csv), near,
// ambiguous, missing (hint), ref / ref-conflict, not-code; plus partial = unique, but tiles in the +-32-byte core differ
// and no operand explains it (a manual look). Problem rows (not exact/masked/ref) -> <out>.problems.csv.
// Also: problem rows placed by function fingerprints (IC names, strings, imports, mapped callers/callees, size and
// instruction mix: <out>.candidates.csv; a placed missing row gets `hint`, a placed ambiguous row becomes `near`), and
// the script assemblies diffed against the Steam ones (--asm, default game-copy/assemblies: <out>.managed.csv, methods
// whose type and name appear in mods/ts3fixes/src flagged). --questions <file> writes a follow-up for the player
// (Sims3PortProbe.ps1 -Questions): instructions at the guessed addresses, dwords at conflicting refs. Data addresses
// (not code) come from refs, IC table slots by name, vtable slots, or globals voted over paired functions.
// shortcut: no vtable slot map (no data section contents in the report; RTTI vtables only when the exe has RTTI);
//   run exe-addr-map on a probe -DumpOut image (dev only) when a port needs vtable slots.
import { readFileSync, writeFileSync, mkdirSync, existsSync, mkdtempSync, rmSync, readdirSync } from "node:fs";
import { join, dirname } from "node:path";
import { tmpdir } from "node:os";
import { inflateRawSync, deflateRawSync } from "node:zlib";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { parsePe, defaultSites, funcOff, tieBreak, makePe, type Row, type RefVote } from "./exe-addr-map.ts";
import { groupDiffs, type Pe, type FieldDiff } from "./exe-port-diff.ts";
import { field } from "./x86-len.ts";
import { fingerprints, assemblies, parseFp, placeRows, parseAsms, managedDiff, mapVts, mapGlobals } from "./port-fingerprints.ts";
import { shiftRuns } from "./exe-port-diff.ts";
import { SIG_FILE, K, CORE, parseSig, sigId, matchImage, buildSigs, formatSig, siteVas, steamId, type Sig } from "./port-sigs.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const hex = (a: number | null | undefined) => (a === null || a === undefined ? "" : "0x" + a.toString(16).toUpperCase().padStart(8, "0"));

// first file of a zip (stored or deflated), enough for the probe's one-file zip
export function readReport(path: string): string {
    const b = readFileSync(path);
    if (b.readUInt32LE(0) !== 0x04034b50) return b.toString("utf8");
    const method = b.readUInt16LE(8), csize = b.readUInt32LE(18), at = 30 + b.readUInt16LE(26) + b.readUInt16LE(28);
    const data = b.subarray(at, at + csize);
    return (method === 8 ? inflateRawSync(data) : data).toString("utf8");
}

export type Parsed = { head: Map<string, string>; S: Map<number, string[]>; F: Map<number, [number, number][]> };
export function parseReport(text: string): Parsed {
    const p: Parsed = { head: new Map(), S: new Map(), F: new Map() };
    for (const l of text.replace(/^﻿/, "").split(/\r?\n/)) {
        if (l.startsWith("S ")) { const w = l.split(" "); p.S.set(+w[1], w.slice(2)); }
        else if (l.startsWith("F ")) { const w = l.split(" "); const a = p.F.get(+w[1]) ?? []; a.push([+w[2], parseInt(w[3], 16)]); p.F.set(+w[1], a); }
        else { const m = l.match(/^([\w-]+):\s*(.*)$/); if (m && !p.head.has(m[1])) p.head.set(m[1], m[2]); }
    }
    return p;
}
const kv = (s: string | undefined, k: string) => (s ?? "").match(new RegExp(`(?:^|\\s)${k}=(\\S+)`))?.[1];

export function toRows(r: Pe, sig: Sig, rep: Parsed): { rows: Row[]; refs: RefVote[]; diffs: FieldDiff[]; obase: number } {
    const obase = parseInt(kv(rep.head.get("pe"), "imagebase") ?? "0", 16) || 0x400000;
    const osize = parseInt(kv(rep.head.get("pe"), "sizeofimage") ?? "0", 16) || 0x2000000;
    const labels = new Map(defaultSites().map((s) => [s.va, s]));
    const votes = new Map<number, Map<number, number>>();
    const vote = (a: number, b: number) => { const m = votes.get(a) ?? new Map(); m.set(b, (m.get(b) ?? 0) + 1); votes.set(a, m); };
    const diffs: FieldDiff[] = [];
    const rows: Row[] = sig.sites.map((s, si) => {
        const l = labels.get(s.va), rva = s.va - sig.base, w = rep.S.get(si) ?? ["-"];
        const row: Row = { ref: s.va, new: null, kind: "missing", count: 0, lo: 0, hi: 0, cands: [], funcOff: funcOff(r, rva), label: l?.label ?? "", where: l?.where ?? "" };
        if (w[0] === "a") { row.kind = "ambiguous"; row.count = +w[1]; row.cands = w.slice(2).map((x) => parseInt(x, 16) - obase); return row; }
        if (w[0] !== "u") return row;
        const nw = parseInt(w[1], 16), lo = +w[2], hi = +w[3], mis = w[4] === "-" ? [] : w[4].split(",").map(Number);
        Object.assign(row, { new: nw, count: 1, lo: rva + lo, hi: rva + hi });
        const tOff = (t: number) => sig.tiles[t].rva - rva;
        let same = mis.length === 0, my: FieldDiff[] = [];
        for (const [fi, v] of rep.F.get(si) ?? []) {
            const F = sig.fields[fi], at = F.rva;
            if (F.kind === "r") {
                const old = r.base + at + 4 + r.img.readInt32LE(at);
                vote(old, v);
                if (v - (nw + (at - rva) + 4) !== old - (r.base + at + 4)) same = false;
            } else if (F.kind === "a") {
                const old = r.img.readUInt32LE(at);
                vote(old, v);
                if (old !== v) same = false;
            } else {
                const signed = F.kind === "d", old = field(r.img, at, F.size, signed);
                const nv = signed && v >= 2 ** (8 * F.size - 1) ? v - 2 ** (8 * F.size) : v;
                const ptr = F.kind === "i" && F.size === 4 && old >= r.base && old < r.base + r.img.length && v >= obase && v < obase + osize;
                if (old !== nv && !ptr) my.push({ at: r.base + at - F.ins, kind: signed ? "disp" : "imm", old, new: nv, size: F.size });
            }
        }
        const coreMis = mis.some((t) => tOff(t) < CORE && tOff(t) + K > -CORE);
        row.kind = my.length ? "offset-changed" : coreMis ? "partial" : same ? "exact" : "masked";
        row.diffs = my; diffs.push(...my);
        return row;
    });
    tieBreak(rows, obase);
    const refs: RefVote[] = [...votes].map(([a, m]) => {
        const best = [...m].sort((p, q) => q[1] - p[1])[0];
        return { ref: a, new: best[0], votes: best[1], conflicts: [...m.values()].reduce((s, v) => s + v, 0) - best[1] };
    }).sort((a, b) => a.ref - b.ref);
    for (const va of sig.notCode) {
        const v = refs.find((x) => x.ref === va), l = labels.get(va);
        rows.push({ ref: va, new: v?.new ?? null, kind: v ? "ref" : "not-code", count: v?.votes ?? 0, lo: 0, hi: 0, cands: [], funcOff: "?", label: l?.label ?? "", where: l?.where ?? "" });
    }
    return { rows, refs, diffs, obase };
}

export const csvLines = (rows: Row[], refs: RefVote[]) => ["ref,new,kind,count,delta,window,funcOff,hint,label,where",
    ...rows.map((x) => [hex(x.ref), hex(x.new), x.kind, x.count, x.new === null ? "" : x.new - x.ref, x.hi - x.lo, x.funcOff, hex(x.hint), `"${x.label.replace(/"/g, '""')}"`, x.where].join(",")),
    ...refs.map((x) => [hex(x.ref), hex(x.new), x.conflicts ? "ref-conflict" : "ref", x.votes, x.new - x.ref, "", "", "", "", ""].join(","))];

const good = (k: string) => k === "exact" || k === "masked" || k === "ref";

function run(reportPath: string, refPath: string, sigPath: string, outArg: string | undefined, all: boolean, asmDir: string, questions?: string) {
    const text = readReport(reportPath), rep = parseReport(text), sigText = readFileSync(sigPath, "utf8"), sig = parseSig(sigText);
    if (rep.head.get("sig") !== sigId(sigText)) { console.error(`report made with signatures ${rep.head.get("sig")}, this file is ${sigId(sigText)}: check out the commit whose tools/port-probe/port-sigs.txt has that id (git log -- tools/port-probe/port-sigs.txt)`); process.exit(2); }
    const buf = readFileSync(refPath);
    if (steamId(buf) !== sig.steam) { console.error(`--ref is not the Steam exe the signatures were built from (${sig.steam})`); process.exit(2); }
    const r = parsePe(buf), { rows, refs, diffs } = toRows(r, sig, rep);
    const exe = rep.head.get("exe"), name = (kv(exe, "name") ?? "other").replace(/\.\w+$/, "") + "-" + (kv(exe, "sha256") ?? "x").slice(0, 8);
    const out = outArg ?? join(repo, "out", "exe-addr-map", name + ".csv"), base = out.replace(/\.csv$/, "");
    mkdirSync(dirname(out), { recursive: true });
    const all_ = text.split(/\r?\n/);
    for (const k of ["exe", "pe", "dump", "game", "packs", "bin", "windows"]) if (rep.head.has(k)) console.log(`${k}: ${rep.head.get(k)}`);
    // place the rows the byte match missed by function fingerprints (docs/68 §7.4)
    const ofp = parseFp(all_), sfp = parseFp(fingerprints({ img: r.img, prefBase: r.base, runBase: r.base }));
    const mapped: [number, number][] = rows.filter((x) => (x.kind === "exact" || x.kind === "masked" || x.kind === "offset-changed") && x.new !== null).map((x) => [x.ref, x.new!]);
    const isProb = (x: Row) => !good(x.kind) && x.kind !== "not-code";
    const { pairs, cands } = placeRows(sfp, ofp, mapped, rows.filter(isProb).map((x) => x.ref));
    const placed = new Set<number>();
    for (const c of cands) {
        if (c.guess === null) continue;
        const x = rows.find((y) => y.ref === c.ref)!;
        placed.add(c.ref);
        if (x.kind === "ambiguous" && x.cands.some((v) => v + parseInt(kv(rep.head.get("pe"), "imagebase") ?? "400000", 16) === c.guess)) { x.kind = "near"; x.new = c.guess; }
        else if (x.new === null) x.hint = c.guess;
    }
    // vtables (slot shifts) and data addresses inside them (patched slots)
    const vm = mapVts(sfp, ofp, pairs), gm = mapGlobals(sfp, ofp, pairs);
    for (const x of rows) {
        if (x.kind !== "not-code") continue;
        const v = vm.find((v) => x.ref >= v.ref && x.ref < v.ref + 4 * v.slots && (x.ref - v.ref) % 4 === 0), i = v ? (x.ref - v.ref) / 4 : 0;
        const ic = sfp.icSlots.get(x.ref), os = ic === undefined ? [] : [...ofp.icSlots].filter(([, n]) => n === ic);
        if (os.length === 1) { x.kind = "ref"; x.new = os[0][0]; x.count = 1; x.label = x.label || `IC slot ${ic}`; }
        else if (v && v.new !== null && v.shift[i] !== null) { x.kind = "ref"; x.new = v.new + 4 * (i + v.shift[i]!); x.count = v.votes; x.label = x.label || "vtable slot"; }
        else if (gm.has(x.ref)) { const g = gm.get(x.ref)!; x.kind = g.conflicts ? "ref-conflict" : "ref"; x.new = g.new; x.count = g.votes; x.label = x.label || "global"; }
    }
    writeFileSync(base + ".vtables.csv", "ref,new,slots,newSlots,votes,shifts\n" + vm.map((v) => [hex(v.ref), hex(v.new), v.slots, v.newSlots, v.votes, `"${shiftRuns(v.shift)}"`].join(",")).join("\n") + "\n");
    const vbad = vm.filter((v) => v.new === null || v.slots !== v.newSlots || v.shift.some((s) => s !== 0)).length;
    writeFileSync(base + ".candidates.csv", "ref,fn,cand,score,second,guess,why\n" + cands.map((c) => [hex(c.ref), hex(c.fn), hex(c.cand), c.score, c.second, hex(c.guess), c.why].join(",")).join("\n") + "\n");
    const lines = csvLines(rows, refs);
    writeFileSync(out, lines.join("\n") + "\n");
    const groups = groupDiffs(diffs);
    writeFileSync(base + ".offsets.csv", "kind,old,new,count,sites\n" + groups.map((g) => [g.kind, g.old, g.new, g.count, g.sites.map(hex).join(" ")].join(",")).join("\n") + "\n");
    const probs = rows.filter((x) => !good(x.kind)), cprobs = refs.filter((x) => x.conflicts);
    const pfile = base + ".problems.csv";
    writeFileSync(pfile, [lines[0], ...csvLines(probs, cprobs).slice(1)].join("\n") + "\n");
    for (const x of (all ? rows : probs).slice(0, all ? Infinity : 40)) console.log(`${hex(x.ref)} ${x.kind}${x.count > 1 ? " " + x.count : ""} ${hex(x.new) || (x.hint ? "hint " + hex(x.hint) : "")}${placed.has(x.ref) ? " (fingerprint)" : ""} ${x.label} ${x.where}`.trimEnd());
    if (!all && probs.length > 40) console.log(`... ${probs.length - 40} more in ${pfile}`);
    const kinds = new Map<string, number>();
    for (const x of rows) kinds.set(x.kind, (kinds.get(x.kind) ?? 0) + 1);
    console.log(`${rows.length} addresses: ${[...kinds].map(([k, n]) => `${k} ${n}`).join(", ")}; same address ${rows.filter((x) => x.new === x.ref).length}; refs ${refs.length} (${cprobs.length} with conflicts); offset groups ${groups.length} -> ${out}`);
    const icBoth = [...sfp.ics.keys()].filter((k) => ofp.ics.has(k)).length;
    console.log(`fingerprints: functions ${sfp.fns.length} Steam / ${ofp.fns.length} other, ${pairs.size} function pairs; problem rows placed ${placed.size} of ${cands.length} -> ${base}.candidates.csv; ICs ${icBoth} of ${sfp.ics.size} Steam names present (${ofp.ics.size} other); RTTI vtables ${ofp.rtti.length}; vtables ${vm.length} Steam, ${vm.filter((v) => v.new !== null).length} mapped, ${vbad} shifted or unmapped -> ${base}.vtables.csv`);
    // script assemblies vs the Steam ones
    let mAi = 0;
    const oasm = parseAsms(all_);
    if (!oasm.size) console.log("assemblies: none in the report");
    else if (!existsSync(asmDir)) console.log(`assemblies: Steam assemblies not found at ${asmDir} (--asm)`);
    else {
        const srcDir = join(repo, "mods", "ts3fixes", "src");
        const src = existsSync(srcDir) ? readdirSync(srcDir).filter((f) => f.endsWith(".cs")).map((f) => readFileSync(join(srcDir, f), "utf8")).join("\n") : "";
        const { rows: md, same } = managedDiff(parseAsms(assemblies(asmDir)), oasm, src);
        writeFileSync(base + ".managed.csv", "module,method,status,ts3fixes\n" + md.map((m) => [m.module, `"${m.method.replace(/"/g, '""')}"`, m.status, m.ts3fixes ? "yes" : ""].join(",")).join("\n") + "\n");
        const c = (st: string, used?: boolean) => md.filter((m) => m.status === st && (used === undefined || m.ts3fixes === used)).length;
        const hot = md.filter((m) => m.ts3fixes && m.status !== "hash-changed");
        mAi = hot.length;
        for (const m of hot.slice(0, all ? Infinity : 20)) console.log(`managed ${m.status} ${m.module} ${m.method}`);
        console.log(`assemblies: ${oasm.size} in the report, same MVID ${same.length} (${same.join(", ") || "-"}); methods size-changed ${c("size-changed")}, hash-only ${c("hash-changed")}, removed ${c("removed")}, added ${c("added")}, missing modules ${c("module-missing")}; named in ts3fixes: ${hot.length} changed/removed + ${c("hash-changed", true)} hash-only -> ${base}.managed.csv`);
    }
    const unplaced = probs.filter((x) => !placed.has(x.ref));
    if (questions) {
        const qs = [...probs.filter((x) => x.new !== null || x.hint).map((x) => ({ va: hex(x.new ?? x.hint!), what: "insn", n: 12, why: hex(x.ref) })),
            ...cprobs.map((x) => ({ va: hex(x.new), what: "dword", n: 1, why: hex(x.ref) }))].slice(0, 200);
        writeFileSync(questions, JSON.stringify({ questions: qs }, null, 1));
        console.log(`questions: ${qs.length} -> ${questions} (player: Sims3PortProbe.ps1 -Questions <file>)`);
    }
    const n = unplaced.length + cprobs.length + mAi;
    console.log(n ? `AI needed: ${n} rows (see ${pfile}, ${base}.candidates.csv, ${base}.managed.csv)` : "AI needed: none");
}

function selfCheck(refPath: string | undefined, ps: boolean) {
    const ok = (c: boolean, what: string) => { if (!c) { console.error(`port-report self-check FAILED: ${what}`); process.exit(1); } };
    // synthetic: as port-sigs' fixture; other = 0x40 bytes inserted at 0x1000, displacement 0x918 -> 0x91C near 0x2100,
    // region around 0x4800 destroyed, a duplicated block at 0x3000/0x5000 (ambiguous unless neighbours decide)
    let seed = 5;
    const rnd = () => (seed = (Math.imul(seed, 1103515245) + 12345) >>> 0) >>> 16 & 0xff;
    const N = 0x6000, ref = Buffer.alloc(N);
    for (let i = 0; i < N; i++) { ref[i] = rnd(); if (ref[i] === 0xe8 || ref[i] === 0xe9) ref[i] = 0x90; }
    ref[0x2000] = 0xe8; ref.writeInt32LE(0x800 - 0x2005, 0x2001);
    ref.fill(0x90, 0x2005, 0x2010); ref[0x2010] = 0xa1; ref.writeUInt32LE(0x407010, 0x2011);  // mov eax,[.data+0x10]
    ref.fill(0x90, 0x2108, 0x2110); Buffer.from([0x8b, 0x81, 0x18, 0x09, 0, 0]).copy(ref, 0x2110);
    ref.copy(ref, 0x5000, 0x3000, 0x3400);
    const ins = 0x40, sh = (o: number) => (o >= 0x1000 ? o + ins : o);
    const other = Buffer.concat([ref.subarray(0, 0x1000), Buffer.alloc(ins, 0xcc), ref.subarray(0x1000), Buffer.alloc(0x1000)]);
    other.writeInt32LE(0x800 - sh(0x2005), sh(0x2001));
    other.writeUInt16LE(0x91c, sh(0x2112));
    other.writeUInt32LE(0x409010, sh(0x2011));  // .data moved by the longer code
    for (let i = 0x4500; i < 0x4b00; i++) other[sh(i)] ^= 0x5a;
    const r = parsePe(makePe(ref, [])), o = parsePe(makePe(other, []));
    const va = (x: number) => 0x401000 + x;
    const sig = parseSig(formatSig(buildSigs(r, [0x300, 0x2000, 0x2100, 0x3200, 0x4800].map(va).concat([0x407010]))));
    const rep = (pe: Pe) => parseReport([`sig: x`, `pe: imagebase=${pe.base.toString(16)} sizeofimage=${pe.img.length.toString(16)}`, ...matchImage({ img: pe.img, exec: pe.exec, prefBase: pe.base, runBase: pe.base }, sig)].join("\n"));
    const id = toRows(r, sig, rep(r));
    ok(id.rows.filter((x) => x.kind !== "not-code" && x.kind !== "ref").every((x) => x.kind === (x.ref === va(0x3200) ? "near" : "exact") && x.new === x.ref) && !id.diffs.length, `identity exact (duplicate near): ${id.rows.map((x) => x.kind).join()}`);
    const m = toRows(r, sig, rep(o)), k = (x: number) => m.rows.find((y) => y.ref === va(x))!;
    ok(k(0x300).kind === "exact" && k(0x300).new === va(0x300), "before insert: exact");
    ok(k(0x2000).kind === "masked" && k(0x2000).new === va(0x2040), `call operand and data pointer moved: masked +0x40 (${k(0x2000).kind})`);
    ok(k(0x2100).kind === "offset-changed" && k(0x2100).new === va(0x2140), `displacement changed: offset-changed (${k(0x2100).kind})`);
    const g = groupDiffs(m.diffs);
    ok(g.length === 1 && g[0].old === 0x918 && g[0].new === 0x91c && g[0].sites[0] === va(0x2110), "offset group disp 0x918 -> 0x91C at the instruction");
    ok(k(0x3200).kind === "near" && k(0x3200).new === va(0x3240), `duplicated block: near by neighbour delta (${k(0x3200).kind})`);
    ok(k(0x4800).kind === "missing" && k(0x4800).hint === va(0x4840), "destroyed: missing with hint");
    ok(m.refs.some((x) => x.ref === va(0x800) && x.new === va(0x800)), "call target voted as ref");
    ok(m.rows.some((x) => x.ref === 0x407010 && x.kind === "ref" && x.new === 0x409010), "data address (not code) answered from refs");
    ok(csvLines(m.rows, m.refs)[0] === "ref,new,kind,count,delta,window,funcOff,hint,label,where", "exe-addr-map CSV header");
    console.log("port-report self-check: synthetic ok");
    const real = refPath ?? join(repo, "game-copy", "bin", "TS3W.exe");
    if (!existsSync(real)) { console.log("port-report self-check: real exe absent, skipped"); return; }
    const buf = readFileSync(real), pe = parsePe(buf);
    const sigText = existsSync(SIG_FILE) && parseSig(readFileSync(SIG_FILE, "utf8")).steam === steamId(buf) ? readFileSync(SIG_FILE, "utf8") : formatSig(buildSigs(pe, siteVas(), steamId(buf)));
    const rs = parseSig(sigText), lines = matchImage({ img: pe.img, exec: pe.exec, prefBase: pe.base, runBase: pe.base }, rs);
    const res = toRows(pe, rs, parseReport([`pe: imagebase=${pe.base.toString(16)} sizeofimage=${pe.img.length.toString(16)}`, ...lines].join("\n")));
    // exact everywhere except windows the masked absolute pointers make identical twins (they resolve to `near`)
    const code = res.rows.filter((x) => x.kind !== "ref" && x.kind !== "not-code"), exact = code.filter((x) => x.kind === "exact" && x.new === x.ref).length;
    const near = code.filter((x) => x.kind === "near" && x.new === x.ref).length;
    ok(exact + near === code.length && near <= 3, `Steam identity: ${exact} exact + ${near} near of ${code.length} (${code.filter((x) => x.kind !== "exact").slice(0, 5).map((x) => hex(x.ref) + " " + x.kind).join(", ")})`);
    ok(res.refs.every((x) => !x.conflicts && x.new === x.ref) && !res.diffs.length, "Steam identity: refs to themselves, no offset diffs");
    console.log(`port-report self-check: Steam identity ${exact} exact + ${near} near (same address) of ${code.length}, refs ${res.refs.length}, not-code ${res.rows.length - code.length}`);
    if (!ps) return;
    const dir = mkdtempSync(join(tmpdir(), "port-probe-")), rp = join(dir, "r.txt");
    try {
        const asmDir = join(dirname(real), "..", "assemblies"), t0 = Date.now();
        execFileSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", join(repo, "tools", "port-probe", "Sims3PortProbe.ps1"), "-ImageFile", real, "-ReportOut", rp, "-SigFile", SIG_FILE, ...(existsSync(asmDir) ? ["-AssemblyDir", asmDir] : [])],
            { stdio: ["ignore", "ignore", "inherit"], timeout: 600000 });
        const text = readReport(rp), body = /^(S|F|summary:|P|M|Z|R|W|I|A|t|m) /;
        const got = text.split(/\r?\n/).filter((l) => body.test(l));
        const want = [...lines, ...fingerprints({ img: pe.img, prefBase: pe.base, runBase: pe.base }), ...(existsSync(asmDir) ? assemblies(asmDir) : [])].filter((l) => body.test(l));
        const bad = want.findIndex((l, i) => l !== got[i]);
        ok(got.length === want.length && bad < 0, `probe C# vs TS: ${got.length}/${want.length} lines, first difference ${bad} (${want[bad]} | ${got[bad]})`);
        const zipped = deflateRawSync(Buffer.from(text), { level: 6 }).length;
        console.log(`port-report self-check: probe C# = TS on the Steam exe${existsSync(asmDir) ? " + assemblies" : ""} (${want.length} lines; report ${(text.length / 1024).toFixed(0)} KB, ~${(zipped / 1024).toFixed(0)} KB zipped; ${((Date.now() - t0) / 1000).toFixed(0)} s)`);
    } finally { rmSync(dir, { recursive: true, force: true }); }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v, positionals } = parseArgs({ allowPositionals: true, options: { ref: { type: "string" }, sigs: { type: "string" }, out: { type: "string" }, asm: { type: "string" }, questions: { type: "string" }, all: { type: "boolean" }, ps: { type: "boolean" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck(v.ref, !!v.ps);
    else if (!positionals[0]) { console.error("usage: node tools/port-report.ts <Sims3PortReport-xxxxxxxx.zip|.txt> [--ref <Steam TS3W.exe>] [--sigs file] [--out file.csv] [--all]"); process.exit(2); }
    else run(positionals[0], v.ref ?? join(repo, "game-copy", "bin", "TS3W.exe"), v.sigs ?? SIG_FILE, v.out, !!v.all, v.asm ?? join(repo, "game-copy", "assemblies"), v.questions);
}
