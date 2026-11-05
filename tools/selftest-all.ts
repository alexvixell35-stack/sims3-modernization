// Compile check + self-check of every tool script, so scripts do not regress (CONTRIBUTING.md "Tool scripts").
//   node tools/selftest-all.ts                 everything (local-only checks run when their data is there)
//   node tools/selftest-all.ts --ci            skip LOCAL_ONLY self-checks (printed as local-only), as CI does
//   node tools/selftest-all.ts --only '<glob>'  only matching paths (repo-relative, e.g. 'tools/harness/*.ts'); repeatable
//   node tools/selftest-all.ts --list          print each script's self-check (args, local-only, none) and exit
//   node tools/selftest-all.ts --self-check    check this runner's own helpers and lists
// Compile check, no script code runs: .js/.mjs/.cjs = `node --check <file>`. .ts/.mts/.cts = node:module
// stripTypeScriptTypes (full TS parse; rejects syntax Node cannot strip, such as enum) then `node --check` on the stripped
// text in a temp .mjs (.cjs for .cts, or when the module parse fails). Plain `node --check x.ts` is not used: on Node 24 it
// passes ESM-looking .ts files without parsing them (a broken file exits 0) and rejects CJS-looking ones on any type.
// Self-checks: ARGS below, else tools/selftest/*/run.ts with no args, else the script's own `--self-check` / `--self-test`
// (found by its parseArgs or argv literal); 4 in parallel, 60 s each unless TIMEOUT_S says more. A check FAILs on a non-zero
// exit or a console.assert failure. Exit 1 on a compile error, FAIL, TIMEOUT, or a script with neither a self-check nor a
// NO_SELF_CHECK entry.
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { tmpdir } from 'node:os';
import { join, matchesGlob, relative, resolve } from 'node:path';
import { parseArgs } from 'node:util';

process.removeAllListeners('warning'); // stripTypeScriptTypes is experimental: its warning is noise here
const ROOT = resolve(import.meta.dirname, '..');
const SCAN = ['tools', 'mods'];
const EXT = /\.(ts|mts|cts|js|mjs|cjs)$/;
const SKIP_DIRS = new Set(['node_modules', 'bin', 'obj', 'build', 'out']);

// Self-checks not started with --self-check / --self-test: the args that run them.
const ARGS: Record<string, string[]> = {
  'tools/check-docs-code.ts': [],              // own self-check first, then scans docs/
  'tools/dbpf.ts': [],
  'tools/github/create-issues.ts': ['--dry-run'], // validates labels/milestones/issues.json, no gh call
  'tools/harness/fixes-require.ts': [],
  'tools/harness/hitch-tasks.ts': [],
  'tools/harness/machine-load.ts': [],         // samples the machine for 15 s (5 s gives no sample)
  'tools/harness/repro-loop.ts': [],           // also validates every loop in repros.json
  'tools/harness/session-reuse.ts': [],
  'tools/harness/story-census.ts': [],
  'tools/harness/story-day.ts': [],
  'tools/harness/story-fuzzer.ts': [],
  'tools/harness/stuck-rule.ts': [],
  'tools/hook-sites.ts': ['--check'],          // own self-check, then every native address must be in docs/68
  'tools/probe/g11-check.mjs': [],
  'tools/probe/vcache-check.mjs': [],
};

// Self-check needs a dev machine (Windows tools, game copy): skipped with --ci, and locally when `needs` is missing.
const CSC = { why: 'Windows .NET Framework csc' };
const LOCAL_ONLY: Record<string, { why: string; needs?: string }> = {
  'tools/harness/machine-load.ts': { why: 'Windows PowerShell counters' },
  'tools/harness/night-run.ts': { why: 'Windows session lock query' },
  'tools/binder-bodies.ts': { why: 'game script assemblies', needs: 'game-copy/assemblies/mscorlib.dll' },
  'tools/offload-override-scan.ts': { why: 'game script assemblies', needs: 'game-copy/assemblies/ScriptCore.dll' },
  'tools/mod-method-diff.ts': { why: 'game script assemblies', needs: 'game-copy/assemblies/ScriptCore.dll' },
  'tools/offload3-closure.ts': { why: 'managed call graph index', needs: 'game-copy/analysis/callgraph/index.json' },
  'tools/selftest/autlive/run.ts': CSC,
  'tools/selftest/autoffload/run.ts': CSC,
'tools/selftest/autshadow/run.ts': CSC,
  'tools/selftest/doormirror/run.ts': CSC,
  'tools/selftest/easpread/run.ts': CSC,
  'tools/selftest/errortrapspread/run.ts': CSC,
  'tools/selftest/farthestpick/run.ts': CSC,
  'tools/selftest/hotspots/run.ts': CSC,
  'tools/selftest/nostart/run.ts': CSC,
  'tools/selftest/overwatchnotice/run.ts': CSC,
  'tools/selftest/owprobe/run.ts': CSC,
  'tools/selftest/overwatchspread/run.ts': CSC,
  'tools/selftest/preplancb/run.ts': CSC,
  'tools/selftest/asyncjigsync/run.ts': CSC,
  'tools/selftest/photosession/run.ts': CSC,
  'tools/selftest/rabbitholedoor/run.ts': CSC,
  'tools/selftest/jazzparam/run.ts': CSC,
  'tools/selftest/buffupdate/run.ts': CSC,
  'tools/selftest/carnoroute/run.ts': CSC,
  'tools/selftest/npcdriverpool/run.ts': CSC,
  'tools/selftest/bookcarry/run.ts': CSC,
  'tools/selftest/ageupabort/run.ts': CSC,
  'tools/selftest/reftable/run.ts': { why: 'Windows csc + game ScriptCore.dll', needs: 'game-copy/assemblies/ScriptCore.dll' },
  'tools/selftest/replandefer/run.ts': CSC,
  'tools/selftest/routesrc/run.ts': CSC,
  'tools/selftest/savercount/run.ts': CSC,
  'tools/selftest/screenfit/run.ts': CSC,
  'tools/selftest/seasonimposter/run.ts': CSC,
  'tools/selftest/compaction/run.ts': CSC,
  'tools/selftest/deadrefs/run.ts': CSC,
  'tools/selftest/outfitwait/run.ts': CSC,
  'tools/selftest/ageupswap/run.ts': CSC,
  'tools/selftest/bakesale/run.ts': CSC,
  'tools/selftest/nullservice/run.ts': CSC,
  'tools/selftest/vacation/run.ts': CSC,
  'tools/selftest/uniformbackup/run.ts': CSC,
  'tools/selftest/convturn/run.ts': CSC,
  'tools/selftest/savecleanup/run.ts': CSC,
  'tools/selftest/slicequeue/run.ts': CSC,
  'tools/selftest/menumaven/run.ts': CSC,
  'tools/selftest/typecache/run.ts': CSC,
  'tools/selftest/unseen/run.ts': CSC,
  'tools/selftest/unseenfb/run.ts': CSC,
  'tools/selftest/venuememo/run.ts': CSC,
  'tools/selftest/wakepark/run.ts': CSC,
};

// No self-check, on purpose (the gap stays visible here). A new script needs a self-check or an entry with its reason.
const LIVE = 'drives the live game through the probe; nothing to check offline';
const ONE_OFF = 'one-off generator of a docs/data table from local game files (game-copy)';
const FRIDA = 'Frida script inside the live game';
const MTS_EXT = 'Chrome extension script (chrome.* APIs); its parsers run in tools/mts-helper/cli/mts.ts --self-check, its rules in tools/mts-helper/build.ts --self-check';
// Paths kept out of the public repo by .gitattributes export-ignore (maintainer-only tools).
const INTERNAL = ['tools/mts-helper/'];
const NO_SELF_CHECK: Record<string, string> = {
  'tools/probe/ep-gate-probe.js': 'Frida script injected into the game; no self-check possible',
  'tools/ai-usage.ts': 'appends the local Claude usage file to docs/ai-usage.md; no logic to test',
  'tools/autonomy-tier2.ts': ONE_OFF,
  'tools/autonomy-virtual-closure.ts': ONE_OFF,
  'tools/autshadow-cha-lists.ts': ONE_OFF,
  'tools/autshadow-guard-lists.ts': ONE_OFF,
  'tools/emu/ts-ref.ts': 'stdin helper of the tools/emu specs, checked by them',
  'tools/extract-assemblies.ts': 'readIndex, decryptS3SA and peImage covered by the tools/dbpf.ts self-test',
  'tools/frida/cold-paths.js': FRIDA,
  'tools/frida/f4b-reads.js': `${FRIDA}; its logic block runs in tools/harness/f4b-report.ts --self-check`,
  'tools/frida/inroom-reads.js': `${FRIDA}; its walker runs in tools/harness/inroom-report.ts --self-check`,
  'tools/frida/live-reads2.js': `${FRIDA}; its logic block runs in tools/harness/live-reads2-report.ts --self-check`,
  'tools/frida/stutter-profile.js': FRIDA,
  'tools/frida/watch-stalk.js': FRIDA,
  'tools/ghidra/icsafety/classify.ts': ONE_OFF,
  'tools/ghidra/icsafety/stats.ts': ONE_OFF,
  'tools/harness/ag.mjs': LIVE,
  'tools/harness/age-save.ts': LIVE,
  'tools/harness/build-agent.ts': 'builder: Windows csc against the game DLLs',
  'tools/harness/cycle.ts': LIVE,
  'tools/harness/focus-game.ts': 'Windows window state of the live game',
  'tools/harness/frame-times.js': FRIDA,
  'tools/harness/new-save.ts': LIVE,
  'tools/harness/offload-phase0.ts': LIVE,
  'tools/harness/profile-world.ts': `${LIVE}; its pure parts are in repro-loop.ts, session-reuse.ts, story-*.ts`,
  'tools/harness/reload-agent.ts': LIVE,
  'tools/harness/repro-save.ts': LIVE,
  'tools/harness/repro.ts': `${LIVE}; repros.json is validated by the repro-loop.ts self-check`,
  'tools/harness/run-check.mjs': 'compares two local profile runs; no logic beyond a threshold',
  'tools/harness/save-diff.ts': 'reads local saves; DBPF parts covered by the tools/dbpf.ts self-test',
  'tools/harness/save-sources.ts': 'reads local saves; DBPF parts covered by the tools/dbpf.ts self-test',
  'tools/harness/save-stats.ts': 'reads local saves; DBPF parts covered by the tools/dbpf.ts self-test',
  'tools/harness/stuck-scan.ts': LIVE,
  'tools/harness/travel-trip.ts': LIVE,
  'tools/harness/verify-shortcut.ts': LIVE,
  'tools/harness/warm-cache.ts': 'reads local game files into the OS cache; no logic to test',
  'tools/harness/watchdog.ts': LIVE,
  'tools/ic-safety-classify.ts': ONE_OFF,
  'tools/internal-calls-map.ts': 'needs TS3W.exe; asserts the Route_Plan address on every run',
  'tools/mono-host-ics.ts': ONE_OFF,
  'tools/mts-helper/ext/src/bg.ts': MTS_EXT,
  'tools/mts-helper/ext/src/job.ts': MTS_EXT,
  'tools/mts-helper/ext/src/options.ts': MTS_EXT,
  'tools/mts-helper/ext/src/poll.ts': MTS_EXT,
  'tools/mts-helper/ext/src/sel.ts': MTS_EXT,
  'tools/mts-helper/ext/src/wizard-main.ts': MTS_EXT,
  'tools/probe/agent.js': `${FRIDA}; its g11 and vcache blocks run in tools/probe/g11-check.mjs and vcache-check.mjs`,
  'tools/probe/ep-gate-probe.js': `${FRIDA} (EA app 1.69 gate probe, docs/234)`,
  'tools/probe/gc-read.mjs': LIVE,
  'tools/probe/managed-sample.mjs': LIVE,
  'tools/probe/profile-compare.mjs': 'prints local profile runs side by side; no logic to test',
  'tools/rng-unit-paths.ts': ONE_OFF,
  'tools/route-plan-callers.ts': ONE_OFF,
  'tools/selftest/nraas-upstream/run.ts': 'is a self-test, but needs an NRaas clone path argument',
  'tools/setup/build-fixes.ts': 'builder: Windows csc against the game DLLs (the C# compile is checked by the native/fixes builds)',
  'tools/setup/build-nraas-upstream.ts': 'builder: needs an NRaas clone',
  'tools/setup/build-overwatch-tuning.ts': 'builder of one tuning package; no logic to test',
  'tools/setup/check-conflicts.ts': 'reads the installed Mods folder; DBPF index covered by the tools/dbpf.ts self-test',
  'tools/setup/install-fixes.ts': 'installer: downloads into the game folder; its content health part is content-health.ts --self-test',
  'tools/tier2-blockers.ts': ONE_OFF,
};

const TIMEOUT_S: Record<string, number> = {};

// args that run the self-check, or undefined when the script has none
export function selfCheckArgs(rel: string, src: string): string[] | undefined {
  if (NO_SELF_CHECK[rel]) return undefined;
  if (ARGS[rel]) return ARGS[rel];
  if (/^tools\/selftest\/[^/]+\/run\.ts$/.test(rel)) return [];
  // the option literal of parseArgs ('self-check') or an argv test ('--self-check'); comments do not count
  const code = src.replace(/^\s*\/\/.*$/gm, '');
  for (const f of ['self-check', 'self-test']) if (new RegExp(`['"\`](--)?${f}['"\`]`).test(code)) return [`--${f}`];
  return undefined;
}

function walk(dir: string, out: string[]) {
  if (!existsSync(dir)) return;
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) { if (!SKIP_DIRS.has(e.name) && !e.name.startsWith('.')) walk(p, out); }
    else if (EXT.test(e.name) && !e.name.endsWith('.d.ts')) out.push(relative(ROOT, p).replaceAll('\\', '/'));
  }
}

type Res = { code: number | null; out: string; timedOut: boolean; ms: number };
function run(args: string[], timeoutS: number): Promise<Res> {
  return new Promise(res => {
    const t0 = Date.now();
    const c = spawn(process.execPath, args, { cwd: ROOT, stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, NODE_NO_WARNINGS: '1' } });
    let out = '', timedOut = false;
    c.stdout.on('data', d => out += d); c.stderr.on('data', d => out += d);
    const t = setTimeout(() => {
      timedOut = true;
      // kill the tree (a self-check may start csc or a test exe)
      if (process.platform === 'win32') spawn('taskkill', ['/T', '/F', '/PID', String(c.pid)], { stdio: 'ignore' }); else c.kill('SIGKILL');
    }, timeoutS * 1000);
    c.on('close', code => { clearTimeout(t); res({ code, out, timedOut, ms: Date.now() - t0 }); });
  });
}

async function pool<T, R>(items: T[], n: number, fn: (t: T) => Promise<R>): Promise<R[]> {
  const out: R[] = new Array(items.length); let i = 0;
  await Promise.all(Array.from({ length: n }, async () => { while (i < items.length) { const k = i++; out[k] = await fn(items[k]); } }));
  return out;
}

let TMP = '';
const strip = (src: string) => stripTypeScriptTypes(src, { mode: 'strip' });
async function compile(rel: string): Promise<string> {
  const abs = join(ROOT, rel);
  if (!/\.(ts|mts|cts)$/.test(rel)) { const r = await run(['--check', abs], 30); return r.code === 0 ? '' : r.out.trim(); }
  let js: string;
  try { js = strip(readFileSync(abs, 'utf8')); } catch (e: any) { return `${rel}: ${e.code ?? ''} ${e.message}`; }
  const base = join(TMP, rel.replaceAll('/', '__'));
  const exts = rel.endsWith('.cts') ? ['.cjs'] : rel.endsWith('.mts') ? ['.mjs'] : ['.mjs', '.cjs'];
  let first = '';
  for (const x of exts) {
    writeFileSync(base + x, js);
    const r = await run(['--check', base + x], 30);
    if (r.code === 0) return '';
    first ||= r.out.trim().replaceAll(base + x, rel);
  }
  return first;
}

const tail = (s: string, n = 15) => s.trimEnd().split(/\r?\n/).slice(-n).map(l => '      ' + l).join('\n');

function selfCheck() {
  const ok = (c: boolean, what: string) => { if (!c) { console.error(`self-check FAILED: ${what}`); process.exit(1); } };
  ok(selfCheckArgs('tools/a.ts', "parseArgs({ options: { 'self-check': { type: 'boolean' } } })")?.[0] === '--self-check', 'parseArgs key');
  ok(selfCheckArgs('tools/a.ts', "if (process.argv.includes('--self-test')) t();")?.[0] === '--self-test', 'argv literal');
  ok(!selfCheckArgs('tools/a.ts', "// node tools/b.ts --self-check\n// 'self-check'\nx();"), 'comment only');
  ok(selfCheckArgs('tools/selftest/x/run.ts', '')?.length === 0, 'selftest runner');
  ok(!selfCheckArgs('tools/selftest/nraas-upstream/run.ts', ''), 'NO_SELF_CHECK wins');
  ok(selfCheckArgs('tools/hook-sites.ts', '')?.[0] === '--check', 'ARGS wins');
  ok(strip('const x: number = 1;').startsWith('const x'), 'strip');
  let threw = false; try { strip('const x: number = ;'); } catch { threw = true; }
  ok(threw, 'strip rejects a syntax error');
  threw = false; try { strip('enum E { A }'); } catch { threw = true; }
  ok(threw, 'strip rejects enum');
  const all = [...Object.keys(ARGS), ...Object.keys(LOCAL_ONLY), ...Object.keys(NO_SELF_CHECK), ...Object.keys(TIMEOUT_S)];
  // internal tools stay out of the public repo (.gitattributes export-ignore), so their entries may be missing there
  for (const k of all) ok(existsSync(join(ROOT, k)) || INTERNAL.some(p => k.startsWith(p)), `listed file exists: ${k}`);
  for (const k of Object.keys(ARGS)) ok(!NO_SELF_CHECK[k], `in ARGS and NO_SELF_CHECK: ${k}`);
  console.log('selftest-all self-check ok');
}

async function main() {
  const { values } = parseArgs({ options: { ci: { type: 'boolean' }, only: { type: 'string', multiple: true }, list: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) return selfCheck();
  const t0 = Date.now();
  const files: string[] = [];
  for (const d of SCAN) walk(join(ROOT, d), files);
  files.sort();
  const sel = values.only?.length ? files.filter(f => values.only!.some(g => matchesGlob(f, g))) : files;
  const args = new Map(sel.map(f => [f, selfCheckArgs(f, readFileSync(join(ROOT, f), 'utf8'))]));
  const unlisted = sel.filter(f => !args.get(f) && !NO_SELF_CHECK[f]);
  if (values.list) {
    for (const f of sel) {
      const a = args.get(f), lo = LOCAL_ONLY[f];
      const what = a ? `self-check: node ${f} ${a.join(' ')}`.trimEnd() : NO_SELF_CHECK[f] ? `none (${NO_SELF_CHECK[f]})` : 'none (UNLISTED)';
      console.log(`${f.padEnd(48)} ${what}${lo ? `  [local-only: ${lo.why}]` : ''}`);
    }
    return;
  }

  TMP = mkdtempSync(join(tmpdir(), 'selftest-all-'));
  const compileErr = await pool(sel, 4, compile);
  rmSync(TMP, { recursive: true, force: true });
  const bad = sel.filter((_, i) => compileErr[i]);
  console.log(`compile: ${sel.length - bad.length} ok, ${bad.length} failed`);
  for (const f of bad) console.log(`  COMPILE FAIL ${f}\n${tail(compileErr[sel.indexOf(f)], 8)}`);

  const checks = sel.filter(f => args.get(f));
  const counts: Record<string, number> = { PASS: 0, FAIL: 0, TIMEOUT: 0, SKIP: 0 };
  await pool(checks, 4, async f => {
    const a = args.get(f)!, lo = LOCAL_ONLY[f];
    const skip = lo && (values.ci ? `local-only: ${lo.why}` : lo.needs && !existsSync(join(ROOT, lo.needs)) ? `local-only, no ${lo.needs}` : '');
    if (skip) { counts.SKIP++; console.log(`  SKIP    ${f} (${skip})`); return; }
    if (bad.includes(f)) { counts.FAIL++; console.log(`  FAIL    ${f} (does not compile)`); return; }
    const r = await run([join(ROOT, f), ...a], TIMEOUT_S[f] ?? 60);
    // console.assert only prints: count its message as a failure
    const st = r.timedOut ? 'TIMEOUT' : r.code === 0 && !/^Assertion failed/m.test(r.out) ? 'PASS' : 'FAIL';
    counts[st]++;
    console.log(`  ${st.padEnd(7)} ${f} ${a.join(' ')} (${(r.ms / 1000).toFixed(1)} s)`);
    if (st !== 'PASS') console.log(tail(r.out));
  });
  const none = sel.filter(f => NO_SELF_CHECK[f]);
  console.log(`self-checks: ${counts.PASS} PASS, ${counts.FAIL} FAIL, ${counts.TIMEOUT} TIMEOUT, ${counts.SKIP} SKIP`);
  console.log(`without self-check: ${none.length} listed in NO_SELF_CHECK${unlisted.length ? `, ${unlisted.length} UNLISTED (add a self-check or a NO_SELF_CHECK entry):` : ''}`);
  for (const f of unlisted) console.log(`  UNLISTED ${f}`);
  console.log(`total ${((Date.now() - t0) / 1000).toFixed(1)} s`);
  if (bad.length || counts.FAIL || counts.TIMEOUT || unlisted.length) process.exit(1);
}

await main();
