// ts3native runtime switches the harness flips in a running game (profile-world.ts --ab and native_set repro steps) and
// session-reuse.ts reads and puts back between jobs that share one game load (docs/170 §2).
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
// ts3native's runtime switches with a /native parameter (native?<name>=0|1), not C# patches; the /native field holding each
export const nativeNames = ['routeworker', 'routebudget', 'routeasync', 'alloc', 'waterjps'];
export const nativeModeFields: Record<string, string> = { routeworker: 'workerMode', routebudget: 'budgetMode', routeasync: 'asyncMode', alloc: 'allocMode', waterjps: 'waterJpsMode' };
// ts3native switches that live only as a Ts3nCounters field (no /native parameter): name -> byte offset
export const counterSwitches: Record<string, number> = { boundcall: 0x6278, gvunseen: 0x6018, equalsfast: 0x5F94, steerretry: 0x5E94, gvattr: 0x5E4C, ufhostlate: 0x5E38, lostremoval: 0x5DE8, kdexemptseen: 0x5D18, kdcutcamrecheck: 0x5D54, castrim: 0x5CF0, jit: 0x5CAC, destroyparkdx: 0x5BCC, idlecheckc2: 0x5BD8, idlecheckc2b: 0x5C0C, idlecheckc: 0x5B88, idlecheckcshadow: 0x5B8C, routetoobjtail: 0x5B78, footstepro: 0x5B34, gcgrowlog: 0x5AE8, unseentablettl: 0x5958, unseenrm: 0x5834, unseenobj: 0x56D4, unseentable: 0x5694, unseenanim: 0x5594, unseenghost: 0x5534, mintcall: 0x54E0, workerwait: 0x54B0, unseenfbfinish: 0x5440, unseenfbc: 0x5390, unseenfb: 0x5360, typegrowth: 0x52F0, sqrtheap: 0x52F4, jobyield: 0x45CC, plandrain: 0x458C, refcountguard: 0x44D4, fbjobdeadline: 0x440C, kinddeadlines: 0x521C, goalgen: 0x43B4, h2ic: 0x43D8, plandeadline: 0x4244, failmemo: 0x4280, a10foreign: 0x4308, heightcache: 0x1B4, simskipshared: 0xBF4, allocdedupe: 0xC1C, lotsincos: 0xC28, idcache: 0xC3C, terraingrave: 0xD00, discourage: 0x1074, discdestroy: 0x135C, discsweep: 0x1360, steerro: 0x10A4, steershadow: 0x10A8, fprebuildlock: 0x1110, lotfdro: 0x1134, lotfdshadow: 0x1138, locowin: 0x13E8, h2: 0x13E8, asyncreplan: 0x14A8, obstructedro: 0x1528, obstructedshadow: 0x152C, slotenabledro: 0x1568, slotenabledshadow: 0x156C, routesnap: 0x2F74, routesnaprel: 0x302C, routesnapgate: 0x3068, routesnapfree: 0x307C, fallbackasync: 0x314C, deferfootprint: 0x3178, m1retry: 0x31C8, destroypark: 0x31E0, avoidro: 0x31F0, avoidshadow: 0x31F4, earlyout: 0x3260, unseenthrottle: 0x33C8, unseenavoid: 0x3424, unseenfootprint: 0x3428, unseenrate: 0x342C, fbjobcap: 0x354C, tailunlock: 0x3878, replandedupe: 0x38FC, gate0ro: 0x3998, gate0shadow: 0x399C, deferfootprint2: 0x3FB4, deferprivatefp: 0x4740, routetoobjro: 0x3FF4, routetoobjroshadow: 0x3FF8, doormemo: 0x408C, placementro: 0x42CC, placementroshadow: 0x42D0, goalvalidro: 0x47E0, goalvalidroshadow: 0x47E4, idlecheckro: 0x4828, idlecheckroshadow: 0x482C, routesnapplan: 0x4660, routesnapshare: 0x4664, routesnapearly: 0x4B7C, holdkeyed: 0x4E7C, routesnapstalelot: 0x4F3C, routesnapkinds: 0x503C, asklent: 0x4FFC, doormemox: 0x5014, routesnapforeign: 0x50DC, routesnapstalelotfp: 0x50FC, ownjobwait: 0x443C };
// repro step {cmd: "native_set", args: {fallbackasync: 1, earlyout: 0, ...}}: flips ts3native switches per loop (ABBA in one
// load) the way --ab does; handled in profile-world.ts, not by the agent. Values are written as given (1/0; a mode number for earlyout).
// Also the two caps the game reads per plan/job (not --ab switches): synccapus = [RouteAsync] SyncCapMs, fbjobcapus = FbJobCapMs, in us.
// docs/164 §7: plandeadlineus / plandeadlineownus = [RouteAsync] PlanDeadlineMs / PlanDeadlineOwnMs in us (needs the ini value > 0 to install)
// unseenfbus (v138, docs/202) = [UnseenFallback] DeadlineMs in us; unseenfbcms (v139, docs/202 §3.4) = SiteWaitMs in ms
// gvunseenms (v189, docs/250 §7.3 B2) = [GoalValidRO] UnseenDeferMaxMs in ms (clamped 100-10000 at use)
// p6warm (v131, docs/189 §16): [P6Waits] arm from the in-load loop (0 = the P6 switch, 1 = off, 2 = on); ladder loops set "$on:2:1"
export const nativeValues: Record<string, number> = { gvunseenms: 0x601C, unseenfbcms: 0x5394, unseenfbus: 0x5364, synccapus: 0x3164, fbjobcapus: 0x3550, plandeadlineus: 0x4248, plandeadlineownus: 0x424C, kdceilus: 0x5D28, jobnameus: 0x5E40, kdnpcceilus: 0x5D48, p6warm: 0x51BC };
// Switches that are their own ts3native export, not a Ts3nCounters field: native_set name -> export (gcgrow.cpp, docs/67 §35)
export const nativeExports: Record<string, string> = { gcstreakat: 'ts3native_gc_streak_at' };

// --ab names and --fixes-on / --fixes-off names (C# patches switches) that name one switch twice: one name in both fixes lists,
// or an --ab name that --ab flips as a patches switch (not native, not a counter switch). A native or counter --ab switch and the
// patches switch of the same name are two switches (2026-11-04: --ab jobyield = [RouteAsync] JobYield, the counter at 0x45CC,
// with --fixes-on jobyield = ts3fixes' step-aside, which only acts while that counter is on: the pair is the A/B of both).
export const abFixesClash = (ab: string[], fixesOn: string[], fixesOff: string[]): string[] =>
  [...new Set([...fixesOn, ...fixesOff])].filter(n => (fixesOn.includes(n) && fixesOff.includes(n)) || (ab.includes(n) && !nativeNames.includes(n) && !(n in counterSwitches)));
// node tools/harness/native-switches.ts --self-check: every offset above names exactly one LONG field of Ts3nCounters in
// ts3native.cpp (its "+0x..." comment), so a counters layout change cannot leave --ab / native_set writing the wrong field
if (process.argv.includes('--self-check') && import.meta.filename === resolve(process.argv[1] ?? '')) {
  const src = readFileSync(new URL('../../native/ts3native/ts3native.cpp', import.meta.url), 'utf8').split(/\r?\n/).filter(l => /^\s*(volatile\s+)?LONG\s+\w+/.test(l));
  const bad: string[] = [];
  for (const [n, o] of Object.entries({ ...counterSwitches, ...nativeValues })) {
    const re = new RegExp(`(?<![\\w+])\\+0x${o.toString(16)}(?!\\w)`, 'i');
    if (src.filter(l => re.test(l.slice(l.indexOf('//')))).length !== 1) bad.push(`${n} +0x${o.toString(16).toUpperCase()}`);
  }
  if (!src.some(l => /\bm2Take;.*\+0x2F74.*--ab routesnap/.test(l))) bad.push('routesnap (m2Take)');
  if (bad.length) { console.error(`native-switches self-check FAILED: no single Ts3nCounters field for ${bad.join(', ')}`); process.exit(1); }
  const clash = [abFixesClash(['jobyield'], ['jobyield'], []), abFixesClash(['routeasync'], [], ['routeasync']), abFixesClash(['idlememo'], ['idlememo'], []), abFixesClash([], ['a'], ['a', 'b'])];
  if (JSON.stringify(clash) !== JSON.stringify([[], [], ['idlememo'], ['a']])) { console.error(`native-switches self-check FAILED: abFixesClash ${JSON.stringify(clash)}`); process.exit(1); }
  console.log(`native-switches self-check ok (${Object.keys(counterSwitches).length + Object.keys(nativeValues).length} offsets)`);
}
