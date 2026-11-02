// One screen from a player's ts3native.log (docs/188) or ts3fixes-doctor zip (tools/player/ts3fixes-doctor.ps1): versions
// and known builds (docs/data/builds.json), exe class, machine, Bin add-ons, script mods with known overlaps
// (docs/data/compat-matrix.csv), one line per session event, exceptions, and the issues to look at (docs/PLAYERS.md
// "Sending a report"; this file, `## Reading a player's log` in docs/188).
//   node tools/player-log-digest.ts <ts3native.log | doctor.zip> [...] [--all] [--json]
//   node tools/player-log-digest.ts --self-check
// The exe must run Steam 1.67 code (edition Steam, the Steam sha256, or every patch site as expected); anything else
// prints "unsupported exe" and stops, since ts3native's addresses do not apply there. Last line: `needs review: ...`.
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { unzip, zip } from './mts-zip.ts';

const ROOT = resolve(import.meta.dirname, '..');
const STEAM_SHA = '10b2423e2368';                // Steam 1.67 TS3W.exe (docs/15), first 12 hex as the log prints it
const TS3W = [0x400000, 0x125a000];              // TS3W.exe image (no ASLR; SizeOfImage 0xE5A000 of the 1.67 exe)
const LOW_FREE_MB = 300;                         // largest free block below this: Error 12 / crash on save territory (docs/66)
// Bin add-ons with a known overlap: why it matters and where it is written up.
const BIN_NOTES: [RegExp, string][] = [
  [/^ApexRadiance\.asi$/i, 'Apex Radiance: ts3native stands down on the switches Apex owns (docs/187)'],
  [/^Sims3Performance\.asi$/i, 'Sims 3 Performance Patch: shared hook sites (docs/68, docs/69)'],
  [/^Sims3SettingsSetter\.asi$/i, 'Sims 3 Settings Setter: shared hook sites (docs/68)'],
];

export interface Session {
  header: string; native?: string; built?: string; fixes?: string; fixesBuild?: string; uiTasks: boolean; fixesLines: number;
  edition?: string; fileVersion?: string; installClass?: string; exeSha?: string; patchOk: number; patchDiffer: string[];
  system: string[]; laa?: string; addons: string[]; scriptMods: string[]; packages?: string;
  events: string[]; exceptions: Map<string, number>; selfRange?: [number, number]; lowFree?: number; exited: boolean; prevCrash: boolean;
  saveFails: string[]; longSaves: string[];
}
export interface Digest { file: string; sessions: Session[]; doctor: string[]; exe: string; supported: boolean; issues: string[]; builds: string[]; overlaps: string[] }

const hex = (n: number) => '0x' + n.toString(16).toUpperCase().padStart(8, '0');
function newSession(header: string): Session {
  return { header, uiTasks: false, fixesLines: 0, patchOk: 0, patchDiffer: [], system: [], addons: [], scriptMods: [], events: [], exceptions: new Map(), exited: false, prevCrash: false, saveFails: [], longSaves: [] };
}

export function parseLog(text: string): Session[] {
  const out: Session[] = [];
  let s: Session | null = null, world = '', save: { n: string; kind: string; up: string; still: number } | null = null;
  for (const raw of text.split(/\r?\n/)) {
    const l = raw.trimEnd();
    let m = l.match(/^ts3native (\d+\.\d+\.\d+) (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)$/);
    if (m) { s = newSession(l); s.native = m[1]; s.events.push(`${m[2].slice(11, 16)} start, ts3native ${m[1]}`); out.push(s); continue; }
    if (!s) { s = newSession('(no header)'); out.push(s); }
    if (/^OK  /.test(l)) s.patchOk++;
    else if (/^(SKIP|FAIL) .*(bytes differ|not as expected)/.test(l)) s.patchDiffer.push(l);
    else if ((m = l.match(/^diag: ts3native \S+ \(built ([^)]*)\)/))) s.built = m[1];
    else if ((m = l.match(/^diag: edition (Steam|not Steam), TS3W\.exe file version (\S+), install class (.+?)(?: - UNSUPPORTED.*)?$/))) [s.edition, s.fileVersion, s.installClass] = [m[1], m[2], m[3]];
    else if ((m = l.match(/^diag: exe TS3W\.exe \d+ bytes, sha256 ([0-9a-f]+)/))) s.exeSha = m[1];
    else if ((m = l.match(/^diag: (cpu|ram|os|gpu|display)\b:? ?(.*)$/))) s.system.push(`${m[1]} ${m[2].replace(/\s+,/g, ',').trim()}`);
    else if ((m = l.match(/^diag: large address aware: (.*)$/))) s.laa = m[1];
    else if ((m = l.match(/^diag: Bin add-ons: (.*)$/))) s.addons = m[1].split(/, (?=\S+\.(?:asi|dll|orig)\b)/);
    else if ((m = l.match(/^diag: script mods \(S3SA packages\): (.*)$/))) s.scriptMods.push(...m[1].split(/(?<=\.package), /i).filter(x => /\.package$/i.test(x)));  // names may hold ", "
    else if ((m = l.match(/^diag: Mods\\Packages (.*?); scan/))) s.packages = m[1];
    else if ((m = l.match(/^crash log: first \d+ first-chance exceptions \(module 0x([0-9A-F]+)-0x([0-9A-F]+)\)/))) s.selfRange = [parseInt(m[1], 16), parseInt(m[2], 16)];
    else if ((m = l.match(/^crash report: the previous session \(([^)]*)\) ended without a clean exit/))) { s.prevCrash = true; s.events.push(`previous session (${m[1]}) ended without a clean exit`); }
    else if ((m = l.match(/^exception ([0-9A-F]{8}) at 0x([0-9A-F]{8})/))) s.exceptions.set(`${m[1]} ${m[2]}`, (s.exceptions.get(`${m[1]} ${m[2]}`) ?? 0) + 1);
    else if ((m = l.match(/^ts3fixes: ts3fixes (\S+) build (\w+)/))) [s.fixes, s.fixesBuild] = [m[1], m[2]];
    else if ((m = l.match(/^ts3fixes: world: ([^,]+), (.*)$/))) world = `${m[1]}, ${m[2].replace(/, objects \d+$/, '')}`;
    else if ((m = l.match(/^diag: world load #(\d+), uptime (\d+) min/))) s.events.push(`+${m[2]} min world load #${m[1]}${world ? `: ${world}` : ''}`), world = '';
    else if ((m = l.match(/^diag: memory at (world load #\d+|\d+ min|exit).*?largest free (\d+) MB/))) {
      const mb = +m[2]; s.lowFree = Math.min(s.lowFree ?? mb, mb);
      if (m[1].startsWith('world load') && s.events.length) s.events[s.events.length - 1] += `; largest free ${mb} MB`;
    } else if ((m = l.match(/^save: start #(\d+), ([^,]+), uptime (\d+) min/))) save = { n: m[1], kind: m[2], up: m[3], still: 0 };
    else if ((m = l.match(/^save: still running after (\d+) s/)) && save) save.still = +m[1];
    else if ((m = l.match(/^save: wait for (.*) took (\d+) ms/))) s.longSaves.push(`save waited ${m[2]} ms for ${m[1]}`);
    else if ((m = l.match(/^save: end #(\d+) result (\d+) after (\d+) ms/))) {
      const kind = save?.n === m[1] ? save.kind : '?', up = save?.n === m[1] ? save.up : '?';
      s.events.push(`+${up} min save #${m[1]} (${kind}): result ${m[2]}${m[2] === '0' ? ' (ok)' : ' (FAILED)'}, ${(+m[3] / 1000).toFixed(1)} s`);
      if (m[2] !== '0') s.saveFails.push(`save #${m[1]} (${kind}) returned ${m[2]}`);
      if (+m[3] >= 60_000) s.longSaves.push(`save #${m[1]} took ${(+m[3] / 1000).toFixed(0)} s`);
      save = null;
    } else if ((m = l.match(/^diag: exit after (\d+) min, (\d+) saves, (\d+) world loads/))) { s.exited = true; s.events.push(`+${m[1]} min exit (${m[2]} saves, ${m[3]} world loads)`); }
    if (l.startsWith('ts3fixes: ')) { s.fixesLines++; if (/\bui tasks\b/.test(l)) s.uiTasks = true; }
  }
  for (const x of out) {
    if (save) x.events.push(`+${save.up} min save #${save.n} (${save.kind}) started, no end line${save.still ? ` (still running after ${save.still} s)` : ''}`);
    if (!x.exited) x.events.push('no exit line: the game crashed, was killed, or is still running');
  }
  return out;
}

const csvRows = (t: string) => t.split(/\r?\n/).slice(1).filter(Boolean).map(l => [...l.matchAll(/"((?:[^"]|"")*)"|([^,]*)/g)].filter((_, i, a) => i < a.length).map(m => (m[1] ?? m[2] ?? '').replace(/""/g, '"')));
function overlapsOf(mods: string[], matrix: string): string[] {
  const rows = csvRows(matrix).map(r => { const [mod, , , overlap, verdict] = r.filter((_, i) => i % 2 === 0); return { mod, overlap, verdict }; });
  const names = mods.map(m => m.replace(/\.package$/i, '').toLowerCase()), out = new Set<string>();
  // prefix: players rename (simler90's `-Steam` package ships as `-Steam+fixes` too)
  for (const r of rows) if (r.verdict === 'overlap' && names.some(n => n.startsWith(r.mod.toLowerCase()))) out.add(`${r.mod}: overlaps ${r.overlap.split(';')[0].trim()}${r.overlap.includes(';') ? ' ...' : ''}`);
  return [...out];
}

export function digest(file: string, logs: string[], doctorText: string | null, buildsJson: string, matrix: string): Digest {
  const sessions = logs.flatMap(parseLog), last = sessions[sessions.length - 1] ?? newSession('(empty)');
  const issues: string[] = [];
  // exe class: from the newest session that printed it
  const ed = [...sessions].reverse().find(x => x.edition) ?? last, sha = [...sessions].reverse().find(x => x.exeSha)?.exeSha;
  let exe: string, supported = true;
  if (ed.edition === 'Steam' || sha === STEAM_SHA) exe = `Steam 1.67 (${ed.installClass ?? 'install class ?'}, file version ${ed.fileVersion ?? '?'}${sha ? `, sha256 ${sha}...` : ''})`;
  else if (ed.edition && ed.patchOk >= 10 && !ed.patchDiffer.length)
    exe = `Steam 1.67 code at all ${ed.patchOk} patch sites, exe not in a Steam folder or changed (install class ${ed.installClass}, file version ${ed.fileVersion}${sha ? `, sha256 ${sha}... not the Steam one: e.g. an LAA-patched copy` : ''})`;
  else if (!ed.edition) exe = 'unknown (no `diag: edition` line: ts3native before 0.36.53, or [UserDiag] off)';
  else { exe = `unsupported exe (${ed.edition}, install class ${ed.installClass}, file version ${ed.fileVersion}, ${ed.patchDiffer.length} patch sites differ)`; supported = false; }
  if (!supported) issues.push('unsupported exe: ts3native supports the Steam TS3W.exe only; the rest of the log is not read');
  else if (/changed|^unknown/.test(exe)) issues.push(`exe: ${exe.split(' (')[0]}`);
  // builds
  const builds: string[] = [];
  try {
    const b = JSON.parse(buildsJson).builds as { name: string; kind: string; version?: string; note?: string }[];
    for (const v of new Set(sessions.map(x => x.native).filter(Boolean))) {
      const hit = b.filter(x => x.kind === 'asi' || x.name.startsWith('ts3native')).filter(x => x.version === v);
      builds.push(`ts3native ${v}: ${hit.length ? hit.map(x => x.name + (x.note && /release/i.test(x.note) ? ` (${x.note.split(' (')[0]})` : '')).join(', ') : 'no build recorded in builds.json'}`);
    }
  } catch { builds.push('builds.json unreadable'); }
  const overlaps = supported ? overlapsOf(last.scriptMods, matrix) : [];
  if (supported) {
    for (const x of sessions) {
      if (x.patchDiffer.length && /^Steam 1\.67 \(/.test(exe)) issues.push(`${x.patchDiffer.length} patch site(s) differ on a Steam exe (another patch there?): ${x.patchDiffer[0].slice(0, 100)}`);
      if (x.prevCrash) issues.push(`the session before ${x.header.replace(/^ts3native \S+ /, '')} ended without a clean exit (crash or kill)`);
      issues.push(...x.saveFails, ...x.longSaves);
      if (x.lowFree !== undefined && x.lowFree < LOW_FREE_MB) issues.push(`largest free block went down to ${x.lowFree} MB (< ${LOW_FREE_MB}: Error 12 / crash on save risk, docs/66)`);
      const other = [...x.exceptions].filter(([k]) => { const a = parseInt(k.split(' ')[1], 16); return !(x.selfRange && a >= x.selfRange[0] && a < x.selfRange[1]); });
      const ours = [...x.exceptions].length - other.length;
      if (ours) issues.push(`${ours} exception address(es) inside ts3native.asi: run tools/crash-symbolize.ts`);
    }
    if (last.laa && !/^yes/.test(last.laa)) issues.push(`TS3W.exe is not large address aware (${last.laa}): 2 GB instead of 4 GB`);
    if (!last.fixesLines) issues.push('no ts3fixes lines: ts3fixes.package not loaded (or before v0.2.0)');
    else if (!last.uiTasks) issues.push(`ts3fixes ${last.fixes ?? '?'} has no \`ui tasks\` field: before v0.2.4, ask for the current release`);
    for (const a of last.addons) for (const [re, note] of BIN_NOTES) if (re.test(a.split(' ')[0])) issues.push(`Bin add-on ${note}`);
    if (overlaps.length) issues.push(`${overlaps.length} script mod(s) with a known overlap (compat-matrix.csv)`);
  }
  const doctor: string[] = [];
  if (doctorText) {
    const L = doctorText.split(/\r?\n/), i = L.findIndex(l => /^== Issues found: \d+ ==/.test(l));
    for (const l of i >= 0 ? L.slice(i + 1) : []) { const m = l.match(/^\s+\d+\. (.*)$/); if (m) doctor.push(m[1]); else if (/^==/.test(l)) break; }
    issues.push(...doctor.map(d => `doctor: ${d}`));
  }
  return { file, sessions, doctor, exe, supported, issues, builds, overlaps };
}

function exceptionLines(x: Session) {
  return [...x.exceptions].sort((a, b) => b[1] - a[1]).map(([k, n]) => {
    const [code, at] = k.split(' '), a = parseInt(at, 16);
    const where = x.selfRange && a >= x.selfRange[0] && a < x.selfRange[1] ? `ts3native.asi+${hex(a - x.selfRange[0])}` : a >= TS3W[0] && a < TS3W[1] ? 'TS3W.exe' : 'another module';
    return `${code} at 0x${at} x${n} (${where})`;
  });
}

export function render(d: Digest, all: boolean): string[] {
  const o: string[] = [], last = d.sessions[d.sessions.length - 1];
  o.push(`player-log-digest: ${d.file}: ${d.sessions.length} session(s)`);
  o.push(`  exe: ${d.exe}`);
  if (!d.supported) { o.push('  stopped: unsupported exe'); }
  else if (last) {
    o.push(`  versions: ts3native ${last.native ?? '?'}${last.built ? ` (built ${last.built})` : ''}, ts3fixes ${last.fixes ?? 'not seen'}${last.fixesBuild ? ` build ${last.fixesBuild}` : ''}, ui tasks field ${last.uiTasks ? 'yes (v0.2.4+)' : 'no'}`);
    for (const b of d.builds) o.push(`  builds: ${b}`);
    o.push(`  machine: ${[last.laa ? `LAA ${last.laa}` : 'LAA ?', ...last.system].join('; ')}`);
    o.push(`  Bin add-ons: ${last.addons.join(', ') || 'none listed'}`);
    o.push(`  mods: ${last.packages ?? '?'}; ${last.scriptMods.length} script mods listed${all && last.scriptMods.length ? `: ${last.scriptMods.join(', ')}` : ''}`);
    for (const v of d.overlaps) o.push(`  overlap: ${v}`);
    for (const x of d.sessions) {
      o.push(`  session ${x.header.replace(/^ts3native /, '')}:`);
      for (const e of x.events) o.push(`    ${e}`);
      const ex = exceptionLines(x);
      if (ex.length) o.push(`    exceptions: ${(all ? ex : ex.slice(0, 3)).join('; ')}${!all && ex.length > 3 ? `; ${ex.length - 3} more (--all)` : ''}`);
    }
  }
  if (d.doctor.length && all) o.push(`  doctor zip: ${d.doctor.length} issue(s)`);
  o.push(`  issues: ${d.issues.length ? '' : 'none'}`);
  d.issues.forEach((s, i) => o.push(`    ${i + 1}. ${s}`));
  o.push(`needs review: ${d.issues.length ? `${d.issues.length} issue${d.issues.length > 1 ? 's' : ''}` : 'none'}`);
  return o;
}

export function readInput(path: string): { logs: string[]; doctor: string | null } {
  const buf = readFileSync(path);
  if (buf.readUInt32LE(0) !== 0x04034b50) return { logs: [buf.toString('utf8').replace(/^\uFEFF/, '')], doctor: null };
  const z = unzip(buf), logs: string[] = [];
  let doctor: string | null = null;
  for (const [name, b] of z) {
    const t = b.toString('utf8').replace(/^\uFEFF/, '');
    if (/(^|\/)ts3native(-\d+)?\.log$/i.test(name)) logs.push(t);
    else if (/(^|\/)ts3fixes-doctor-[^/]*\.txt$/i.test(name)) doctor = t;
  }
  return { logs, doctor };
}

function selfCheck() {
  let n = 0;
  const t = (c: unknown, m: string) => { console.assert(c, m); if (!c) process.exitCode = 1; n++; };
  // a made-up log in the shapes ts3native 0.36.148 writes (no player data)
  const log = (edition: string, sha: string, extra = '') => [
    'ts3native 0.36.148 2026-10-31 10:00:00', 'host TS3W.exe base 0x00400000', 'crash log: first 16 first-chance exceptions (module 0x66000000-0x66200000)',
    'crash report: the previous session (ts3native 0.36.147) ended without a clean exit: hard report written',
    ...Array.from({ length: 12 }, (_, i) => `OK   detour site ${i} at 0x00${(0x500000 + i * 16).toString(16).toUpperCase()}`),
    'diag: ts3native 0.36.148 (built Oct 31 2026), send this whole file (ts3native.log next to TS3W.exe) with a report',
    `diag: edition ${edition}`, 'diag: cpu: Example CPU        , 8 logical processors', 'diag: ram: 16384 MB', 'diag: os: Windows 6.2.9200', 'diag: large address aware: yes',
    `diag: exe TS3W.exe 14510400 bytes, sha256 ${sha}..., TS3W.exe.orig absent, steam_api.dll present v1`,
    'diag: Bin add-ons: ApexRadiance.asi 11040 KB v2.18.3.0, d3d9.dll 7672 KB v10.0.17763.1 (DXVK), ts3native.asi 1102 KB',
    'diag: script mods (S3SA packages): NRaas_ErrorTrap.package, Some Mod, With Comma.package, ts3fixes.package',
    'diag: Mods\\Packages 3 .package files, 12 MB, 3 with scripts (S3SA), 0 other (CC, tuning), 0 folders deeper than 5 not read; scan 1 ms',
    'ts3fixes: ts3fixes 0.2.4 build 0123abcd, 35 settings on/valued, 45 off', 'ts3fixes: play: ui tasks 0 -> 1 by X',
    'ts3fixes: world: Example.world, lots 10 (residential 5, with a household 3), households 4 with 9 Sims (active household 2), Sims in the world 7, objects 100',
    'diag: world load #1, uptime 2 min, ts3native 0.36.148, mods 3 packages (3 script) 12 MB',
    'diag: memory at world load #1: private 1758 MB, committed 2175 MB; limit 4 GB, free 1605 MB, largest free 1497 MB (2nd 18), session low 1497 MB',
    'exception C0000005 at 0x66001234 (info 00000000 00000000) thread 1 eip 66001234 esp 0; stack code refs: 00D7D56D',
    'exception C0000005 at 0x678A3B48 (info 00000000 00000000) thread 1 eip 678A3B48 esp 0; stack code refs: 00D7D56D',
    'exception C0000005 at 0x678A3B48 (info 00000000 00000000) thread 1 eip 678A3B48 esp 0; stack code refs: 00D7D56D',
    'save: start #1, normal save, uptime 20 min, save mode 1, flush mode 0 (previous flush done)', 'save: end #1 result 0 after 9000 ms',
    'diag: memory at 30 min: private 2900 MB; free 400 MB, largest free 250 MB (2nd 18), session low 250 MB',
    'save: start #2, travel save, uptime 31 min, save mode 1, flush mode 0 (previous flush done)', 'save: still running after 65 s: phase write', 'save: end #2 result 13 after 70000 ms',
    extra,
  ].join('\r\n');
  const B = JSON.stringify({ builds: [{ name: 'ts3native.asi.x', kind: 'asi', version: '0.36.148', note: 'v9.9 release asi' }] });
  const M = 'mod,file,kind,overlap,verdict,evidence\n"NRaas_ErrorTrap","Packages/NRaas_ErrorTrap.package","script+tuning","OverwatchNotice (x); OverwatchSpread (y)","overlap","z"\n';
  const ok = digest('a.log', [log('Steam, TS3W.exe file version 1.67.2.024002, install class Steam library', STEAM_SHA, 'diag: exit after 40 min, 2 saves, 1 world loads')], null, B, M);
  const s = ok.sessions[0], txt = render(ok, false).join('\n');
  t(ok.supported && ok.exe.startsWith('Steam 1.67 (Steam library'), 'Steam exe');
  t(s.native === '0.36.148' && s.fixes === '0.2.4' && s.uiTasks && s.fixesBuild === '0123abcd', 'versions + ui tasks');
  t(ok.builds[0] === 'ts3native 0.36.148: ts3native.asi.x (v9.9 release asi)', 'builds.json');
  t(s.scriptMods.length === 3 && s.scriptMods[1] === 'Some Mod, With Comma.package', 'script mod names with a comma');
  t(ok.overlaps[0] === 'NRaas_ErrorTrap: overlaps OverwatchNotice (x) ...', 'compat-matrix overlap');
  t(s.events.some(e => e === '+2 min world load #1: Example.world, lots 10 (residential 5, with a household 3), households 4 with 9 Sims (active household 2), Sims in the world 7; largest free 1497 MB'), 'world load event');
  t(s.events.includes('+20 min save #1 (normal save): result 0 (ok), 9.0 s') && s.events.includes('+31 min save #2 (travel save): result 13 (FAILED), 70.0 s'), 'save events');
  t(s.events.includes('+40 min exit (2 saves, 1 world loads)') && s.exited, 'exit event');
  t(ok.issues.includes('save #2 (travel save) returned 13') && ok.issues.includes('save #2 took 70 s'), 'save issues');
  t(ok.issues.some(i => i.startsWith('largest free block went down to 250 MB')), 'low memory issue');
  t(ok.issues.some(i => i.startsWith('1 exception address(es) inside ts3native.asi')), 'exception inside ts3native');
  t(txt.includes('C0000005 at 0x678A3B48 x2 (another module)') && txt.includes('C0000005 at 0x66001234 x1 (ts3native.asi+0x00001234)'), 'exception lines');
  t(ok.issues.some(i => i.startsWith('Bin add-on Apex Radiance')) && ok.issues.some(i => i.includes('ended without a clean exit')), 'add-on + previous crash');
  t(txt.split('\n').pop()!.startsWith('needs review: '), 'last line');
  // moved / LAA-patched Steam exe: every patch site as expected
  const moved = digest('b.log', [log('not Steam, TS3W.exe file version 0.0.0.32, install class other folder - UNSUPPORTED, ts3native supports the Steam TS3W.exe only (other exes skip patches whose bytes differ)', 'b78a3d535535')], null, B, M);
  t(moved.supported && moved.exe.startsWith('Steam 1.67 code at all 12 patch sites') && moved.sessions[0].events.at(-1)!.startsWith('no exit line'), 'moved exe + no exit');
  // another exe: patch sites differ -> stop
  const other = digest('c.log', [log('not Steam, TS3W.exe file version 1.69.47.024017, install class EA app - UNSUPPORTED, x', 'aaaaaaaaaaaa', 'SKIP alloc sites EA heap at 0x00401000: bytes differ (00)')], null, B, M);
  const otxt = render(other, false);
  t(!other.supported && otxt.includes('  stopped: unsupported exe') && !otxt.some(l => l.includes('session ')), 'unsupported exe stops');
  // doctor zip: report + log
  const z = zip([['ts3fixes-doctor-20261031-1000.txt', Buffer.from('== copies ==\r\n  x\r\n== Issues found: 1 ==\r\n  1. Old ts3fixes (before v0.2.4): C:\\Users\\<user>\\x.package\r\n')], ['ts3native.log', Buffer.from('\uFEFF' + log('Steam, TS3W.exe file version 1.67.2.024002, install class Steam library', STEAM_SHA))]]);
  const tmp = join(tmpdir(), `pld-selfcheck-${process.pid}.zip`);
  writeFileSync(tmp, z);
  try {
    const r = readInput(tmp), dz = digest('z.zip', r.logs, r.doctor, B, M);
    t(r.logs.length === 1 && dz.sessions[0].native === '0.36.148', 'zip log read (BOM stripped)');
    t(dz.issues.includes('doctor: Old ts3fixes (before v0.2.4): C:\\Users\\<user>\\x.package'), 'doctor issues');
  } finally { rmSync(tmp, { force: true }); }
  console.log(`player-log-digest self-check: ${n} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const { values: v, positionals } = parseArgs({ allowPositionals: true, options: { all: { type: 'boolean' }, json: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
  if (v['self-check']) selfCheck();
  else {
    if (!positionals.length) { console.error('usage: node tools/player-log-digest.ts <ts3native.log | doctor.zip> [...] [--all] [--json]'); process.exit(2); }
    const B = readFileSync(join(ROOT, 'docs', 'data', 'builds.json'), 'utf8');
    const M = existsSync(join(ROOT, 'docs', 'data', 'compat-matrix.csv')) ? readFileSync(join(ROOT, 'docs', 'data', 'compat-matrix.csv'), 'utf8') : 'mod\n';
    let bad = 0;
    for (const p of positionals) {
      const r = readInput(p), d = digest(basename(p), r.logs, r.doctor, B, M);
      if (!r.logs.length) d.issues.unshift('no ts3native.log in this file');
      bad += d.issues.length;
      if (v.json) console.log(JSON.stringify({ ...d, sessions: d.sessions.map(x => ({ ...x, exceptions: Object.fromEntries(x.exceptions) })) }, null, 2));
      else console.log(render(d, !!v.all).join('\n'));
    }
    if (bad) process.exitCode = 1;
  }
}
