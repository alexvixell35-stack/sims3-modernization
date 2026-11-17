// EA app 1.69.47 ini variants from the shipped Steam ini (build.yml native-ea169; docs/232 §5 rows 6-7):
//   ts3native-ea169.ini     minimal: every integer key 0 except the ALLOW / ALLOW_ON sections (env, space separated)
//   ts3native-ea169-rc.ini  release candidate: shipped values kept for every section that is clean on EA, zeroed otherwise
//   ts3native-ea169-test.ini  player test build: the rc ini plus TEST_SET
// Clean = none of the section's source files (tools/ini-sections.json, plus the cpp files that include a flagged .h) has
// a `// unresolved` row in ts3addr-ea169.h or a `// bytes-bad` row in ts3bytes-ea169.h, it is not on the always-off list, and
// none of its dependencies (DEPENDS, from the "Needs ..." notes in ts3native.ini and the SKIP lines in the cpp files) is zeroed.
//   node tools/ea-ini.ts [--out native/ts3native/out-ea169] [--md table.md] | --self-check
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";
import { eaOnly, generated, rawAsmRows, scanText, sourceFiles } from "./addr-codemod.ts";

const repo = join(dirname(fileURLToPath(import.meta.url)), "..");
const SRC = join(repo, "native", "ts3native");

// the section's file has no EA table row on purpose (raw 1.69.47 addresses, no Steam counterpart): allowed in the minimal ini
export const EA_ONLY = new Set(["EaDiag"]);
// kept off on EA whatever the tables say (diagnostics). SteerRO, FpRebuildLock, Watchpoint, RouteToObjRO and MemDiag left this
// list with docs/238 (struct offsets same on 1.69, every address of theirs checked, §5): shipped Enabled=0, so allowed, not on.
// Verify left it 2026-11-13: its 89 probe prologues are in the bytes table now (patch-bytes-check reads the kVf row macros)
export const ALWAYS_OFF: Record<string, string> = {
    EaDiag: "EA-only diagnostics, log only, minimal ini only",
};
// single keys the rc ini zeroes in a kept section: on in the Steam ini, not yet run on EA
// The other rows came with the merge of main (2026-11-06): on in the Steam ini, off on the port, never run on EA. Each leaves here after its EA smoke.
// [UnseenRootMotion] Mode left 2026-11-13: all 29 unseenrm.cpp addresses compared on the decrypted exe (code 12 instructions,
// the steering slot 0x10445C0 -> 0x108F5D0 holds 0x954C70 = TS3A(0x954580)), every byte expectation ok or ported
export const RC_ZERO_KEYS: Record<string, string[]> = {};
const rcZeroKey = (sec: string, key: string) => (RC_ZERO_KEYS[sec] ?? []).includes(key);
// One key of a section that is clean otherwise: its unresolved rows zero that key on EA instead of flagging the whole file (eafix.cpp, wastar.cpp,
// rtoro.cpp and idlero.cpp hold other, working features). The key comes back by itself once port-run resolves its addresses.
export const KEY_ADDRS: Record<string, number[]> = {
    "EaCrashFixes.ThumbPoseBones": [0x00D5B708, 0x00D5B768, 0x00D5B7A1, 0x00FC2D30], "EaCrashFixes.LotGridMiss": [0x0082B608, 0x0082B7F4],
    "RouteToObjRO.Tail": [0x00686A90, 0x0082A780, 0x01003D7C],
    "IdleCheckRO.SiteC": [0x00620D20, 0x00624F70, 0x00652470, 0x0075598B, 0x00755A12, 0x00755A6D, 0x00755A82, 0x00755A97],
    // master fold 2026-11-15 (docs/232 §8): master code newer than rc1111g, no EA 1.69 rows yet; each feature also SKIPs in the EA
    // asi. RacesBits01 and SatbColdPark name no ini key (the code masks Races to bit 2, EA_SET ships Races=4; the cold park is not
    // switched): they only keep autraces.cpp and satb.cpp from zeroing the whole [AutShadow]
    "AutShadow.RacesBits01": [0x004D9AD0, 0x007935B0, 0x008A79B0, 0x009B0530, 0x00C88D80, 0x00D79CA0, 0x0114C908, 0x0114CA08, 0x0114CA0C, 0x011AF670, 0x011C7984, 0x011CB928, 0x011CC928, 0x011CC964, 0x011CDD00, 0x011CE2D0, 0x011CF490, 0x011D3E28, 0x011D4068, 0x011D4BC0, 0x011D6A78, 0x011D7518, 0x011D8888, 0x011D9848, 0x011DA058, 0x011E8FA8, 0x011E8FC0, 0x011E8FC4, 0x011E90F8, 0x011E9290, 0x011E92A8, 0x011E94F8, 0x011E96C1, 0x011EB490, 0x011EBAE0, 0x011ECEED, 0x011ED780, 0x011EE140, 0x011EE520, 0x011EE528, 0x011EE54C, 0x011EE550, 0x011EE554, 0x011EE558, 0x011EE55C, 0x011EE560, 0x011EE564, 0x011EE568, 0x011EE56C, 0x011EE570, 0x011EE574, 0x011EE578, 0x011EE57C, 0x011EE580, 0x011EE584, 0x011EE588],
    "AutShadow.SatbColdPark": [0x011F4130, 0x011F43E4, 0x011F4574, 0x011F45A0],
    "MonoThread.QuitStop": [0x00E534D0],
    "MonoJit.BoundCall": [0x00DA4200, 0x00DBFAE0, 0x00DBFBF0, 0x00DDD880, 0x00DDD900],
    "RouteAsync.FbApplyLog": [0x00631A60, 0x00C68960],
    "ResourceCache.TrimAfterCleanup": [0x00FFE2F0],
    "ResourceCache.TrimEveryMin": [0x00FFE2F0],
    "EaCrashFixes.HeightsInfLoop": [0x0064CEBA, 0x0064CEC0, 0x0064CF55, 0x0064D275],
    "EaCrashFixes.BridgeHeightNaN": [0x00B745A0, 0x00C6133B, 0x00C61342, 0x00C6137B, 0x00C6A46B, 0x00C6A472, 0x00C6A4B3, 0x00C6A709, 0x00C6A710, 0x00C6A772],
    "EaCrashFixes.LotLevelPastEnd": [0x00AB70B0, 0x010652D4],
    "EaCrashFixes.FglStaleObject": [0x00B11A95, 0x00B11A9C, 0x00B11ABF],
};
const keyed = new Set(Object.values(KEY_ADDRS).flat());
const unresolvedRows = (addrHdr: string) => [...addrHdr.matchAll(/^\/\/ unresolved: (\S+) \S+ (?:native\/ts3native\/)?([\w-]+\.(?:cpp|h))/gm)];
// "Section.Key" of every key with an unresolved address
export const keysOff = (addrHdr: string) => { const un = new Set(unresolvedRows(addrHdr).map((m) => parseInt(m[1], 16))); return new Set(Object.keys(KEY_ADDRS).filter((k) => KEY_ADDRS[k].some((a) => un.has(a)))); };
// EA-only shipped defaults: values both EA inis take in place of the Steam ini's (sections that are not zeroed only); the Steam
// ts3native.ini keeps its own value (main ships 1024 too since Run 8q, window mode). BlackListCapKB: docs/232 Run 8n/8o, Run 8p (no leak).
// ThreadStack.Skip: SimulatorThread_Proc, a runtime string the address table cannot rewrite; the Steam value left the EA sim thread capped.
// AutShadow.Races: bit 2 (R2c) only, as v0.2.12 EA shipped it; bits 0-1 have no EA 1.69 rows (docs/232 §8)
export const EA_SET: Record<string, string> = { "GC.BlackListCapKB": "1024", "ThreadStack.Skip": "0x76C360", "AutShadow.Races": "4" };
// EA-port-only keys (TS3N_EA_PORT in ts3addr.h: compiled into the EA asi only, so the Steam ts3native.ini has no line for them):
// appended to the end of their section; a section ts3native.ini lacks goes at the end of the file. Values as the v0.2.10 EA
// rc ini shipped them: SimNoTexture on (player crash, deterministic repro, the fix passes it, docs/67 §34.5), ImageGuard off
// (build.yml ALLOW_ON turns it on in the minimal ini, as on v0.2.10 EA).
export const EA_ADD: Record<string, string[]> = {
    EaCrashFixes: [
        "; EA build only: SimNoTexture=1 (docs/67 §34, crash at 0x5EBE31 / EA 0x5EC891, also seen on vanilla): when a Sim's composite",
        "; texture could not be created (seen near the end of address space), the outfit build read its width through a null pointer.",
        "; The build now skips that composite, the path the game already takes for a texture too wide. That Sim build has no",
        "; composite texture. One \"ea fix: SimNoTexture\" line for each of the first 8.",
        "; SimNoTextureRepro=0 TEST KEY (docs/67 §34): 1 = every Sim composite texture create fails, N = every Nth. With SimNoTexture=0 it",
        "; crashes at the first Sim build by design. Never a player setting.",
        "; SimNoTextureAtlas=0 (docs/67 §34.6, sibling site EA 0x5E16FA / Steam 0x5E082A, no crash report yet): the same read in the",
        "; household atlas build. 1 = a missing texture skips that composite. Static only, never run live.",
        "SimNoTexture=1", "SimNoTextureAtlas=0", "SimNoTextureRepro=0",
    ],
    ImageGuard: [
        "; EA build only. Size check on the raster buffer of the game's image decode (docs/232 Run 8d, imgguard.cpp). The",
        "; decode (PNG, JPEG, DDS, TGA, BMP, GIF) sizes its pixel buffer as (bits per pixel / 8) * width * height from the image",
        "; header, unchecked: a huge or corrupt image asked for ~1.9 GB at the main menu on the EA app build (run 8c), the",
        "; allocation failed and the game crashed in msvcr80. Every decode of LogMB or more is logged as an \"IMAGEGUARD\" line",
        "; (size, codec, resource key when the caller has one, caller address). Over MaxMB, or a size that wraps in 32 bits:",
        "; \"IMAGEGUARD REJECT\", and the decode returns its normal failure, so the game skips that image instead of crashing.",
        "Enabled=0", "LogMB=64", "MaxMB=512",
    ],
};
// ts3native.ini lines with EA_ADD in place (blank lines before the next section stay after the added keys)
export function addEaKeys(lines: string[], add: Record<string, string[]> = EA_ADD): string[] {
    const out: string[] = [], done = new Set<string>();
    let sec = "", tail: string[] = [];
    const flush = () => { if (add[sec] && !done.has(sec)) out.push(...add[sec]), done.add(sec); out.push(...tail); tail = []; };
    for (const l of lines) {
        const m = /^\[(\w+)\]/.exec(l);
        if (m) { flush(); sec = m[1]; out.push(l); continue; }
        if (l.trim() === "") tail.push(l); else { out.push(...tail, l); tail = []; }
    }
    flush();
    for (const s of Object.keys(add)) if (!done.has(s)) out.push("", "[" + s + "]", ...add[s]);
    return out;
}
// the player test build's settings on top of the rc ini (docs/232 "Test build"): the crash guard ([MemDiag] Blockers and
// [Decompress] RepeatLog are ea169-port only, not in the v0.2.11 tree)
export const TEST_SET: Record<string, string> = { "CrashReport.Enabled": "1" };
// a file that only serves one key group of a section (GC: memexp.cpp reads SqrtHeapLimit, shipped 0) does not count for that section
export const IGNORE_FILE: Record<string, string[]> = { GC: ["memexp.cpp"] };
const RG = ["RouteGate", "RouteAsync"];
export const DEPENDS: Record<string, string[]> = {
    RouteGate: ["RouteAsync"], RouteAsync: ["RouteGate"], Discourage: RG, RouteSnap: RG, Watchpoint: ["RouteSnap"],
    SteerRO: RG, FpRebuildLock: RG, LotFrontDoorRO: RG, ObstructedRO: RG, AvoidRO: RG, RouteToObjRO: [...RG, "SlotEnabledRO"], PlacementRO: RG,
    GoalValidRO: RG, IdleCheckRO: RG, SlotEnabledRO: RG, Gate0RO: RG, ProbeP1b: RG, LocoWindow: RG, ProbeP10: RG,
    UnseenProbe: ["RouteAsync"], UnseenThrottle: ["RouteAsync"], UnseenFallback: ["RouteAsync"], HitchAttr: ["RouteAsync"],
    CellMemoProbe: ["RouteJobPhases"], SaveFlush: ["SaveWrite"], Compaction: ["UserDiag", "SaveWrite"], MemDiag: ["UserDiag"], AutShadow: ["MonoThread", "RouteSnap"],
};

export type Row = { section: string; shipped: string; state: "kept" | "zeroed"; reason: string };

// files whose table has a problem: `// unresolved: 0x.. kind native/ts3native/f.cpp:12` and `// bytes-bad: kind 0x.. f.cpp:12`; a flagged .h flags the cpp files including it
export function badFiles(addrHdr: string, bytesHdr: string, includes: Map<string, string[]>): Map<string, string> {
    const bad = new Map<string, string>();
    const add = (f: string, why: string) => { for (const g of [f, ...(includes.get(f) ?? [])]) if (!bad.has(g)) bad.set(g, why); };
    for (const m of unresolvedRows(addrHdr)) if (!keyed.has(parseInt(m[1], 16))) add(m[2], "unresolved row");
    for (const m of bytesHdr.matchAll(/^\/\/ bytes-bad: (\S+) \S+ ([\w-]+\.(?:cpp|h))/gm)) add(m[2], "bytes " + m[1]);
    return bad;
}

export function rcPlan(sections: string[], enabled: Record<string, string>, map: Record<string, string[]>, bad: Map<string, string>): Row[] {
    const why = new Map<string, string>();
    for (const s of sections) {
        if (ALWAYS_OFF[s]) { why.set(s, "always off: " + ALWAYS_OFF[s]); continue; }
        const hit = (map[s] ?? []).filter((f) => bad.has(f) && !(IGNORE_FILE[s] ?? []).includes(f));
        if (hit.length) why.set(s, hit.map((f) => `${f} (${bad.get(f)})`).join(", "));
    }
    for (let changed = true; changed;) {
        changed = false;
        for (const s of sections) {
            const dep = (DEPENDS[s] ?? []).find((d) => why.has(d));
            if (!why.has(s) && dep) { why.set(s, "depends on [" + dep + "], zeroed"); changed = true; }
        }
    }
    return sections.map((s) => ({ section: s, shipped: enabled[s] ?? "-", state: why.has(s) ? "zeroed" : "kept", reason: why.get(s) ?? "files clean" }));
}

// integer keys of the listed sections become 0 (TailPad/InitobjFix run independent of Enabled); paths and names stay
function render(lines: string[], zero: (sec: string) => boolean, force: (sec: string) => boolean, zeroKey: (sec: string, key: string) => boolean = () => false, set: Record<string, string> = {}): string {
    let sec = "";
    return lines.map((l) => {
        const m = /^\[(\w+)\]/.exec(l);
        if (m) { sec = m[1]; return l; }
        if (force(sec) && /^Enabled=/.test(l)) return "Enabled=1";
        const k = /^(\w+)=-?\d+\s*$/.exec(l);
        if (k && (zero(sec) || zeroKey(sec, k[1]))) return k[1] + "=0";
        const v = k ?? /^(\w+)=0x[0-9A-Fa-f]+\s*$/.exec(l);  // a hex value is an address: settable, never zeroed
        return v && set[sec + "." + v[1]] !== undefined ? v[1] + "=" + set[sec + "." + v[1]] : l;
    }).join("\r\n");
}

function load() {
    const ini = addEaKeys(readFileSync(join(SRC, "ts3native.ini"), "utf8").split(/\r?\n/));
    const sections: string[] = [], enabled: Record<string, string> = {};
    let sec = "";
    for (const l of ini) { const m = /^\[(\w+)\]/.exec(l); if (m) { sec = m[1]; sections.push(sec); } const e = /^Enabled=(-?\d+)/.exec(l); if (e && sec) enabled[sec] = e[1]; }
    const includes = new Map<string, string[]>();
    for (const f of readdirSync(SRC).filter((f) => f.endsWith(".cpp"))) for (const m of readFileSync(join(SRC, f), "utf8").matchAll(/^#include "([\w-]+\.h)"/gm)) includes.set(m[1], [...(includes.get(m[1]) ?? []), f]);
    const map = JSON.parse(readFileSync(join(repo, "tools", "ini-sections.json"), "utf8"));
    const addrHdr = readFileSync(join(SRC, "ts3addr-ea169.h"), "utf8"), bad = badFiles(addrHdr, readFileSync(join(SRC, "ts3bytes-ea169.h"), "utf8"), includes);
    return { ini, sections, enabled, map, bad, off: keysOff(addrHdr) };
}

function selfCheck() {
    const ok = (c: boolean, w: string) => { if (!c) { console.error("ea-ini self-check FAILED: " + w); process.exit(1); } };
    const bad = badFiles("// unresolved: 0x00000001 missing native/ts3native/a.cpp:3\n// unresolved: 0x2 xref-none native/ts3native/h.h:1\n", "// bytes-bad: mismatch 0x3 b.cpp:9\n", new Map([["h.h", ["c.cpp"]]]));
    ok([...bad.keys()].sort().join() === "a.cpp,b.cpp,c.cpp,h.h", "badFiles " + [...bad.keys()]);
    const kh = "// unresolved: 0x0082B608 no-ea-evidence native/ts3native/wastar.cpp:197\n";
    ok(badFiles(kh, "", new Map()).size === 0 && [...keysOff(kh)].join() === "EaCrashFixes.LotGridMiss" && keysOff("").size === 0, "keyed row");
    const map = { A: ["a.cpp"], B: ["ok.cpp"], E: ["b.cpp", "ok.cpp"], RouteGate: ["ok.cpp"], RouteAsync: ["a.cpp"], AvoidRO: ["ok.cpp"], GC: ["memexp.cpp", "ok.cpp"] };
    const r = rcPlan(["A", "B", "EaDiag", "E", "RouteGate", "RouteAsync", "AvoidRO", "GC"], { A: "1", B: "1" }, map, new Map([...bad, ["memexp.cpp", "unresolved row"]]));
    const st = Object.fromEntries(r.map((x) => [x.section, x.state]));
    ok(st.A === "zeroed" && st.B === "kept" && st.EaDiag === "zeroed" && !ALWAYS_OFF.Verify && st.E === "zeroed" && st.RouteGate === "zeroed" && st.AvoidRO === "zeroed" && st.GC === "kept", "plan " + JSON.stringify(st));
    ok(render(["[A]", "Enabled=1", "Path=x", "[B]", "Enabled=0"], (s) => s === "A", (s) => s === "B") === "[A]\r\nEnabled=0\r\nPath=x\r\n[B]\r\nEnabled=1", "render");
    ok(!["SteerRO", "FpRebuildLock", "Watchpoint", "RouteToObjRO", "MemDiag"].some((s) => ALWAYS_OFF[s]), "docs/238 sections allowed");
    ok(render(["[GC]", "Enabled=1", "BlackListCapKB=0", "[Z]", "BlackListCapKB=0"], () => false, () => false, undefined, EA_SET) === "[GC]\r\nEnabled=1\r\nBlackListCapKB=1024\r\n[Z]\r\nBlackListCapKB=0", "render set");
    ok(/^BlackListCapKB=1024\s*$/m.test(render(readFileSync(join(SRC, "ts3native.ini"), "utf8").split(/\r?\n/), () => false, () => false, rcZeroKey, EA_SET)), "EA rc ini must carry BlackListCapKB=1024");
    ok(/^Skip=0x76C360\s*$/m.test(render(readFileSync(join(SRC, "ts3native.ini"), "utf8").split(/\r?\n/), () => false, () => false, rcZeroKey, EA_SET)), "EA rc ini exempts the EA sim thread");
    const rc = render(readFileSync(join(SRC, "ts3native.ini"), "utf8").split(/\r?\n/), () => false, () => false, rcZeroKey, EA_SET);
    ok(/^Light=(\d)/m.exec(rc.slice(rc.indexOf("\r\n[MemDiag]\r\n")))?.[1] === "1", "EA rc ini keeps [MemDiag] Light=1");
    const test = render(readFileSync(join(SRC, "ts3native.ini"), "utf8").split(/\r?\n/), () => false, () => false, rcZeroKey, { ...EA_SET, ...TEST_SET }).split("\r\n");
    const base = rc.split("\r\n"), changed = test.filter((l, i) => l !== base[i]);
    ok(test.length === base.length && [...changed].sort().join() === Object.entries(TEST_SET).map(([k, v]) => k.split(".")[1] + "=" + v).sort().join(), "test ini differs from rc only by TEST_SET: " + changed);
    ok(addEaKeys(["[A]", "x=1", "", "[B]", "y=1"], { A: ["k=2"], C: ["z=3"] }).join() === "[A],x=1,k=2,,[B],y=1,,[C],z=3", "addEaKeys");
    const eaRc = render(addEaKeys(readFileSync(join(SRC, "ts3native.ini"), "utf8").split(/\r?\n/)), () => false, () => false, rcZeroKey, EA_SET);
    ok(/^SimNoTexture=1\s*$/m.test(eaRc) && /\r\n\[ImageGuard\]\r\n(;[^\r]*\r\n)*Enabled=0\r\n/.test(eaRc), "EA rc ini: SimNoTexture=1, [ImageGuard] Enabled=0");
    console.log("ea-ini self-check: ok");
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const { values: v } = parseArgs({ options: { out: { type: "string" }, md: { type: "string" }, "self-check": { type: "boolean" } } });
    if (v["self-check"]) selfCheck();
    else {
        const { ini, sections, enabled, map, bad, off } = load();
        // a raw TS3W address in __asm keeps its Steam value on EA (ThumbStall 0xD5A9D2 crash): no EA ini until it is per-build
        const asm = rawAsmRows(sourceFiles().flatMap((f) => { const t = readFileSync(join(SRC, f), "utf8"); return generated(t) || eaOnly(t) ? [] : scanText(t, f); }));
        if (asm.length) throw new Error("raw TS3W address in inline asm (per-build literal under #ifdef TS3A_TABLE + static_assert, as mintcall.cpp): " + asm.map((r) => `${r.file}:${r.line} ${r.lit}`).join(", "));
        const zeroKey = (s: string, k: string) => off.has(s + "." + k);
        const on = (process.env.ALLOW_ON ?? "").split(" ").filter(Boolean), allow = (process.env.ALLOW ?? "").split(" ").filter(Boolean).concat(on);
        for (const s of allow) {
            if (!map[s]) throw new Error(`[${s}] is not in tools/ini-sections.json`);
            const hit = EA_ONLY.has(s) ? [] : map[s].filter((f: string) => bad.has(f));
            if (hit.length) throw new Error(`[${s}] reads ${hit.join(", ")}, which has unresolved (0) or bytes-bad rows in the EA tables: it cannot be on for EA`);
        }
        const out = v.out ?? join(SRC, "out-ea169");
        mkdirSync(out, { recursive: true });
        writeFileSync(join(out, "ts3native-ea169.ini"), render(ini, (s) => !allow.includes(s), (s) => on.includes(s), zeroKey, EA_SET));
        console.log(`minimal ini: on = ${allow.map((s) => "[" + s + "]").join(" ")}; ${bad.size} source files have unresolved or bytes-bad rows`);
        const plan = rcPlan(sections, enabled, map, bad), zeroed = new Set(plan.filter((r) => r.state === "zeroed").map((r) => r.section));
        writeFileSync(join(out, "ts3native-ea169-rc.ini"), render(ini, (s) => zeroed.has(s), () => false, (s, k) => rcZeroKey(s, k) || zeroKey(s, k), EA_SET));
        writeFileSync(join(out, "ts3native-ea169-test.ini"), render(ini, (s) => zeroed.has(s), () => false, (s, k) => rcZeroKey(s, k) || zeroKey(s, k), { ...EA_SET, ...TEST_SET }));
        for (const r of plan) console.log(`rc [${r.section}] shipped Enabled=${r.shipped}: ${r.state}${r.state === "zeroed" ? " (" + r.reason + ")" : ""}`);
        console.log(`keys off for unresolved rows: ${[...off].join(" ") || "none"}`);
        console.log(`rc ini: ${plan.length - zeroed.size} sections kept as shipped (Enabled=1: ${plan.filter((r) => r.state === "kept" && r.shipped === "1").map((r) => r.section).join(" ")}), ${zeroed.size} zeroed`);
        if (v.md) writeFileSync(v.md, "| section | shipped Enabled | rc state | reason |\n|---|---|---|---|\n" + plan.map((r) => `| [${r.section}] | ${r.shipped} | ${r.state} | ${r.reason} |`).join("\n") + "\n");
    }
}
