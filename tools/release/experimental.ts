// Daily experimental pre-release for testers (docs/279): latest master, every switch whose correctness gate PASSED on.
//   node tools/release/experimental.ts [--dry-run] [--ref master] [--switches <json>] [--ignore-busy] [--force] [--keep 7]
//   node tools/release/experimental.ts --self-check
// Steps: skip when a chain / queue / job / the game runs (track.ts status; the scheduled task retries hourly) unless
// --ignore-busy; skip when the last experimental pre-release was built from the same commit and switch list (unless --force)
// or today's tag exists; build in a detached worktree (probe-out/experimental/wt, never the main checkout's build/ or Bin/):
// selftest-all, native build.cmd (asi + selftest.exe), ts3fixes package; apply docs/data/verified-switches.json (verified ->
// on values, blocked -> off values, unverified -> master default) to the ini and the package tuning; scan the files for personal
// names / paths / emails; --dry-run stops here. Otherwise: public sync (tools/publish-public.ts sync --yes), GitHub
// pre-release v<next patch>-exp.<YYYYMMDD PHT> (prerelease, never latest) with ts3native-<tag>.zip (asi + ini) and
// ts3fixes.package, then prune: keep the newest --keep experimental pre-releases. Stable releases and builds.json are untouched.
// Windows only for the build (MSVC, csc); the pure parts (switch apply, tags, prune) are platform-free and self-checked.
// gh uses PERSONAL_GITHUB_TOKEN (else GH_TOKEN); the token is never printed.
import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { zip, unzip, vet } from '../mts-zip.ts';
import { leftovers, enabledSections } from '../publish-public.ts';
import { TYPE_S3SA, fnv64, s3saEncode, writePackage } from '../dbpf.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const TYPE_XML = 0x0333406c; // tools/setup/build-fixes.ts
const EXP_TAG = /^v(\d+)\.(\d+)\.(\d+)-exp\.(\d{8})$/;

type Values = { ini?: Record<string, Record<string, string>>; tunables?: Record<string, string> };
export type Entry = { id: string; status: 'verified' | 'blocked' | 'unverified'; on?: Values; off?: Values; evidence?: string[]; reason?: string };
export type SwitchFile = { schema: number; switches: Entry[]; knownIssues?: string[] };

// --- pure parts (self-checked) ---

// values to apply: verified -> on, blocked -> off, unverified -> nothing (master default stays)
export function plan(f: SwitchFile): Values {
  const out: Required<Values> = { ini: {}, tunables: {} };
  for (const e of f.switches) {
    const v = e.status === 'verified' ? e.on : e.status === 'blocked' ? e.off : undefined;
    if (e.status !== 'unverified' && !v) throw new Error(`${e.id}: status ${e.status} needs its ${e.status === 'verified' ? 'on' : 'off'} values`);
    for (const [s, kv] of Object.entries(v?.ini ?? {})) for (const [k, x] of Object.entries(kv)) {
      const prev = out.ini[s]?.[k];
      if (prev !== undefined && prev !== x) throw new Error(`[${s}] ${k}: ${e.id} wants ${x}, another entry ${prev}`);
      (out.ini[s] ??= {})[k] = x;
    }
    for (const [k, x] of Object.entries(v?.tunables ?? {})) {
      if (out.tunables[k] !== undefined && out.tunables[k] !== x) throw new Error(`${k}: ${e.id} wants ${x}, another entry ${out.tunables[k]}`);
      out.tunables[k] = x;
    }
  }
  if (out.ini.Experimental?.HouseholdToo !== undefined && out.ini.Experimental.HouseholdToo !== '0')
    throw new Error('[Experimental] HouseholdToo must stay 0 in experimental builds (CLAUDE.md household rule)');
  return out;
}

// set key=value inside [section]; every key must already exist there (a typo fails instead of adding a dead key)
export function applyIni(ini: string, set: Values['ini'] = {}): string {
  const lines = ini.split('\n');
  for (const [sec, kv] of Object.entries(set)) for (const [k, v] of Object.entries(kv)) {
    let cur = '', hit = 0;
    for (let i = 0; i < lines.length; i++) {
      const t = lines[i].replace(/\r$/, '');
      if (/^\[.*\]/.test(t)) { cur = t.slice(1, t.indexOf(']')); continue; }
      if (cur === sec && t.startsWith(`${k}=`)) { lines[i] = `${k}=${v}${lines[i].endsWith('\r') ? '\r' : ''}`; hit++; }
    }
    if (hit !== 1) throw new Error(`ini [${sec}] ${k}: found ${hit} times, want 1`);
  }
  return lines.join('\n');
}

export function applyTunables(xml: string, set: Values['tunables'] = {}): string {
  for (const [k, v] of Object.entries(set)) {
    const re = new RegExp(`<${k} value="[^"]*" />`, 'g');
    const n = xml.match(re)?.length ?? 0;
    if (n !== 1) throw new Error(`tunable ${k}: found ${n} times in the package tuning, want 1`);
    xml = xml.replace(re, `<${k} value="${v}" />`);
  }
  return xml;
}

// next tag: patch after the newest stable release, dated in PHT (UTC+8)
export function nextTag(stable: string, nowMs: number): string {
  const m = /^v(\d+)\.(\d+)\.(\d+)$/.exec(stable);
  if (!m) throw new Error(`newest stable release ${stable}: want vX.Y.Z`);
  const d = new Date(nowMs + 8 * 3600_000).toISOString().slice(0, 10).replace(/-/g, '');
  return `v${m[1]}.${m[2]}.${Number(m[3]) + 1}-exp.${d}`;
}

export type Rel = { tagName: string; isPrerelease: boolean; isDraft: boolean; createdAt: string };
export const newestStable = (rs: Rel[]) => rs.filter(r => !r.isPrerelease && !r.isDraft && /^v\d+\.\d+\.\d+$/.test(r.tagName))
  .sort((a, b) => b.createdAt.localeCompare(a.createdAt))[0]?.tagName;
export const expReleases = (rs: Rel[]) => rs.filter(r => r.isPrerelease && EXP_TAG.test(r.tagName)).sort((a, b) => b.createdAt.localeCompare(a.createdAt));
export const toPrune = (rs: Rel[], keep: number) => expReleases(rs).slice(keep).map(r => r.tagName);

// the release body carries what it was built from, so the next run can skip an unchanged master
export const marker = (sha: string, sw: string) => `<!-- exp-source: ${sha} switches: ${sw} -->`;
export function readSource(body: string): { sha: string; sw: string } | null {
  const m = /<!-- exp-source: ([0-9a-f]{7,40}) switches: ([0-9a-f]{8,64}) -->/.exec(body);
  return m ? { sha: m[1], sw: m[2] } : null;
}

// track.ts status lines -> what is running ('' = idle)
export function busy(status: string): string {
  const get = (k: string) => new RegExp(`^${k}:\\s+(.*)$`, 'm').exec(status)?.[1].trim() ?? '';
  const b = ['job', 'game', 'chains', 'queues', 'hold'].map(k => [k, get(k)]).filter(([, v]) => v && v !== '-');
  return b.map(([k, v]) => `${k} ${v}`).join('; ');
}

// personal names / home paths (publish-public scrub list) in any file, emails in text files
export function personal(name: string, b: Buffer, text: boolean): string[] {
  const s = b.toString('latin1');
  const hits = leftovers(s).map(p => `${name}: matches ${p}`);
  if (text) for (const m of s.match(/[\w.+-]+@[\w-]+\.(?:com|net|org|se|io|dev)\b/gi) ?? []) hits.push(`${name}: email-like ${m}`);
  return hits;
}

export function notes(o: { tag: string; stable: string; repo: string; pubSha: string; native: string; ini: string; pkgSha: string; zipName: string; zipSha: string; f: SwitchFile; src: string; sw: string }): string {
  const vals = (v?: Values) => [...Object.entries(v?.ini ?? {}).flatMap(([s, kv]) => Object.entries(kv).map(([k, x]) => `[${s}] ${k}=${x}`)),
    ...Object.entries(v?.tunables ?? {}).map(([k, x]) => `${k}=${x}`)].map(x => `\`${x}\``).join(', ');
  const row = (e: Entry, v?: Values) => `- **${e.id}**: ${vals(v)}. ${e.reason ?? ''}${e.evidence?.length ? ` Evidence: ${e.evidence.map(x => `\`${x}\``).join(', ')}.` : ''}`;
  const on = o.f.switches.filter(e => e.status === 'verified'), off = o.f.switches.filter(e => e.status === 'blocked');
  return [
    `**Experimental build for testers. Not a stable release.** Built automatically from the latest development code (public commit ${o.pubSha.slice(0, 9)}), with every switch whose correctness test passed turned on. It has had less testing than the stable release: back up your saves (\`Documents\\Electronic Arts\\The Sims 3\\\`) before you play, and go back to the stable [${o.stable}](https://github.com/${o.repo}/releases/tag/${o.stable}) if anything goes wrong.`,
    '',
    `Steam edition (1.67) only. EA app players: stay on ${o.stable} (its \`ts3native-ea169\` zip).`,
    '',
    '## Install',
    `Same as the stable release (docs/PLAYERS.md "How to install"): \`ts3native.asi\` and \`ts3native.ini\` from \`${o.zipName}\` into \`The Sims 3\\Game\\Bin\\\`, \`ts3fixes.package\` into \`Documents\\Electronic Arts\\The Sims 3\\Mods\\Packages\\\`. Replace all three files together. Report problems with your \`ts3native.log\` and the release name in a GitHub issue.`,
    '',
    '## Switched on beyond the defaults (correctness test passed)',
    on.length ? on.map(e => row(e, e.on)).join('\n') : '- none',
    '',
    '## Kept off on purpose',
    off.length ? off.map(e => row(e, e.off)).join('\n') : '- none',
    '',
    'Everything else is at the development defaults (CHANGELOG.md "Unreleased" lists what changed since the stable release).',
    '',
    '## Known issues',
    o.f.knownIssues?.length ? o.f.knownIssues.map(x => `- ${x}`).join('\n') : '- none known',
    '',
    '## Files',
    `- \`${o.zipName}\`: ts3native ${o.native} (\`ts3native.asi\`) + experimental \`ts3native.ini\`, SHA-256 \`${o.zipSha}\``,
    `- \`ts3fixes.package\`: SHA-256 \`${o.pkgSha}\``,
    `- Enabled ts3native sections: ${enabledSections(o.ini).join(' ')}`,
    '',
    'Built locally by the maintainer\'s daily experimental job (tools/release/experimental.ts, docs/279), not by CI: `ts3fixes.package` needs EA\'s game files and both files come from the same build. Experimental builds are pruned: only the newest 7 stay.',
    '',
    marker(o.src, o.sw),
  ].join('\n') + '\n';
}

// --- side effects ---

const PHT = () => new Date(Date.now() + 8 * 3600_000).toISOString().slice(0, 19).replace('T', ' ');
let LOG = '';
const log = (s: string) => { const l = `[${PHT()} PHT] ${s}`; console.log(l); if (LOG) appendFileSync(LOG, l + '\n'); };
const sha256 = (b: Buffer | string) => createHash('sha256').update(b).digest('hex');
const ghEnv = () => ({ ...process.env, GH_TOKEN: process.env.PERSONAL_GITHUB_TOKEN || process.env.GH_TOKEN || '', GH_PROMPT_DISABLED: '1', GIT_TERMINAL_PROMPT: '0' });
function run(cmd: string, args: string[], o: { cwd?: string; timeout?: number; env?: NodeJS.ProcessEnv; show?: boolean } = {}): string {
  try {
    return execFileSync(cmd, args, { cwd: o.cwd, timeout: o.timeout ?? 120_000, killSignal: 'SIGKILL', env: o.env ?? process.env, maxBuffer: 1 << 30,
      stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim();
  } catch (e: any) {
    const tail = `${e.stdout ?? ''}\n${e.stderr ?? ''}`.trim().split('\n').slice(-15).join('\n');
    throw new Error(`${cmd} ${args.slice(0, 3).join(' ')}${e.code === 'ETIMEDOUT' ? ' timed out' : ` exit ${e.status}`}\n${tail}`);
  }
}
const gh = (args: string[], timeout = 120_000) => run('gh', args, { env: ghEnv(), timeout });

function main() {
  const { values: a } = parseArgs({ options: {
    'dry-run': { type: 'boolean' }, 'self-check': { type: 'boolean' }, 'ignore-busy': { type: 'boolean' }, force: { type: 'boolean' },
    ref: { type: 'string', default: 'master' }, switches: { type: 'string' }, keep: { type: 'string', default: '7' },
    repo: { type: 'string', default: 'rodneypantonial/sims3-modernization' }, work: { type: 'string', default: join(ROOT, 'probe-out', 'experimental') },
  } });
  if (a['self-check']) return selfCheck();
  const work = resolve(a.work!), wt = join(work, 'wt'), dry = !!a['dry-run'];
  mkdirSync(work, { recursive: true }); LOG = join(work, 'experimental.log');
  if (process.platform !== 'win32') throw new Error('the build needs Windows (MSVC for ts3native, csc for ts3fixes)');
  if (!process.env.PERSONAL_GITHUB_TOKEN && !process.env.GH_TOKEN) throw new Error('PERSONAL_GITHUB_TOKEN (or GH_TOKEN) is not set');

  const st = run(process.execPath, [join(ROOT, 'tools/harness/track.ts'), 'status'], { cwd: ROOT, timeout: 60_000 });
  const b = busy(st);
  if (b && !a['ignore-busy']) { log(`skip: busy (${b}); the scheduled task tries again next hour`); return; }
  if (b) log(`busy but --ignore-busy: ${b}`);

  const sha = run('git', ['rev-parse', a.ref!], { cwd: ROOT });
  const rels: Rel[] = JSON.parse(gh(['release', 'list', '-R', a.repo!, '-L', '100', '--json', 'tagName,isPrerelease,isDraft,createdAt']));
  const stable = newestStable(rels); if (!stable) throw new Error('no stable release found');
  const tag = nextTag(stable, Date.now());
  log(`source ${a.ref} = ${sha}, stable ${stable}, tag ${tag}${dry ? ' (dry run)' : ''}`);

  if (!existsSync(wt)) run('git', ['worktree', 'add', '-q', '--detach', wt, sha], { cwd: ROOT, timeout: 300_000 });
  else { run('git', ['checkout', '-q', '--force', '--detach', sha], { cwd: wt, timeout: 300_000 }); run('git', ['clean', '-fdxq'], { cwd: wt, timeout: 300_000 }); }
  const swPath = a.switches ? resolve(a.switches) : join(wt, 'docs/data/verified-switches.json');
  if (!existsSync(swPath)) throw new Error(`no switch list ${swPath}`);
  const swText = readFileSync(swPath, 'utf8'), f: SwitchFile = JSON.parse(swText), sw = sha256(swText).slice(0, 16), p = plan(f);

  const last = expReleases(rels)[0];
  const prev = last ? readSource(gh(['release', 'view', last.tagName, '-R', a.repo!, '--json', 'body', '-q', '.body'])) : null;
  if (prev && sha.startsWith(prev.sha) && prev.sw === sw && !a.force) { log(`skip: ${last.tagName} already has ${sha.slice(0, 9)} and this switch list`); return; }
  if (rels.some(r => r.tagName === tag)) { log(`skip: ${tag} exists (one experimental release a day)`); return; }

  log('gate: selftest-all'); run(process.execPath, ['tools/selftest-all.ts'], { cwd: wt, timeout: 1_800_000 });
  log('gate: native build.cmd + selftest.exe');
  const nat = join(wt, 'native', 'ts3native');
  run('cmd', ['/d', '/c', 'call .\\build.cmd out-exp'], { cwd: nat, timeout: 1_800_000 }); // .\: NoDefaultCurrentDirectoryInExePath hides a bare build.cmd
  log('build: ts3fixes.package');
  const bdir = join(work, 'build');
  run(process.execPath, ['tools/setup/build-fixes.ts', '--tag', tag], { cwd: wt, timeout: 900_000,
    env: { ...process.env, TS3_BUILD_DIR: bdir, TS3_REF_DIR: process.env.TS3_REF_DIR ?? join(ROOT, 'game-copy', 'assemblies') } });

  const asi = readFileSync(join(nat, 'out-exp', 'ts3native.asi'));
  const native = /#else\s*\r?\n#define TS3NATIVE_VERSION "([^"]+)"/.exec(readFileSync(join(nat, 'ts3native.cpp'), 'latin1'))?.[1] ?? '?';
  if (!asi.includes(`ts3native ${native}`) && !asi.includes(native)) throw new Error(`asi has no version string ${native}`);
  const ini = applyIni(readFileSync(join(nat, 'out-exp', 'ts3native.ini'), 'latin1'), p.ini);
  const basePkg = readFileSync(join(bdir, 'ts3fixes.package')).toString('latin1');
  const xml0 = /<\?xml[\s\S]*?<\/base>/.exec(basePkg)?.[0]; if (!xml0) throw new Error('no tuning XML in ts3fixes.package');
  const pkg = writePackage([
    { type: TYPE_S3SA, group: 0, instance: fnv64('Ts3Fixes'), data: s3saEncode(readFileSync(join(bdir, 'Ts3Fixes.dll'))) },
    { type: TYPE_XML, group: 0, instance: fnv64('Ts3Fixes.Instantiator'), data: Buffer.from(applyTunables(xml0, p.tunables), 'utf8') },
  ]);
  vet('asi', 'ts3native.asi', asi, native); vet('ini', 'ts3native.ini', Buffer.from(ini, 'latin1'), native); vet('package', 'ts3fixes.package', pkg, native);

  const out = join(work, 'out', tag); mkdirSync(out, { recursive: true });
  const zipName = `ts3native-${tag}.zip`, z = zip([['ts3native.asi', asi], ['ts3native.ini', Buffer.from(ini, 'latin1')]]);
  if (unzip(z).get('ts3native.ini')?.toString('latin1') !== ini) throw new Error('zip read-back mismatch');
  writeFileSync(join(out, zipName), z); writeFileSync(join(out, 'ts3fixes.package'), pkg); writeFileSync(join(out, 'ts3native.ini'), ini, 'latin1');
  log(`artifacts ${out}: ${zipName} ${sha256(z).slice(0, 12)}, ts3fixes.package ${sha256(pkg).slice(0, 12)}, ts3native ${native}`);

  const mk = (pubSha: string) => notes({ tag, stable, repo: a.repo!, pubSha, native, ini, pkgSha: sha256(pkg), zipName, zipSha: sha256(z), f, src: sha, sw });
  const hits = [...personal('ts3native.asi', asi, false), ...personal('ts3fixes.package', pkg, false), ...personal('ts3native.ini', Buffer.from(ini, 'latin1'), true),
    ...personal('notes', Buffer.from(mk('0'.repeat(40))), true)];
  if (hits.length) throw new Error(`personal data in the release files:\n${hits.join('\n')}`);
  log('scan: no personal names, home paths or emails');
  if (dry) { writeFileSync(join(out, 'notes.md'), mk('<public sha>')); log(`dry run done: ${out} (nothing published)`); return; }

  log('publish: public sync');
  const msgFile = join(out, 'sync-msg.txt');
  writeFileSync(msgFile, `🔖 Experimental build ${tag}\n\nPublic sync for the experimental pre-release ${tag} (tools/release/experimental.ts).\n`);
  const so = run(process.execPath, ['tools/publish-public.ts', 'sync', '--ref', sha, '--yes', '--message-file', msgFile], { cwd: ROOT, timeout: 3_600_000, env: ghEnv() });
  const pubSha = /merged as ([0-9a-f]{40})/.exec(so)?.[1] ?? gh(['api', `repos/${a.repo}/commits/main`, '-q', '.sha']);
  log(`public commit ${pubSha}`);
  writeFileSync(join(out, 'notes.md'), mk(pubSha));
  gh(['release', 'create', tag, join(out, zipName), join(out, 'ts3fixes.package'), '-R', a.repo!, '--prerelease', '--latest=false', '--target', pubSha,
    '--title', `${tag} (experimental, for testers)`, '--notes-file', join(out, 'notes.md')], 600_000);
  const made = JSON.parse(gh(['release', 'view', tag, '-R', a.repo!, '--json', 'isPrerelease,url']));
  const latest = gh(['release', 'view', '-R', a.repo!, '--json', 'tagName', '-q', '.tagName']);
  if (!made.isPrerelease || latest !== stable) throw new Error(`after publish: prerelease ${made.isPrerelease}, latest ${latest} (want ${stable}): fix by hand`);
  log(`published ${made.url} (prerelease; latest stays ${stable})`);

  const after: Rel[] = JSON.parse(gh(['release', 'list', '-R', a.repo!, '-L', '100', '--json', 'tagName,isPrerelease,isDraft,createdAt']));
  for (const t of toPrune(after, Number(a.keep))) { gh(['release', 'delete', t, '-R', a.repo!, '--cleanup-tag', '-y']); log(`pruned ${t}`); }
}

function selfCheck() {
  const ini = '[GC]\r\nEnabled=1\r\n[RouteSnap]\r\nEnabled=1\r\nDoorMemoX=3\r\nDoorMemoXMs=250\r\n[Experimental]\r\nHouseholdToo=0\r\n';
  const f: SwitchFile = { schema: 1, switches: [
    { id: 'dmx', status: 'blocked', on: { ini: { RouteSnap: { DoorMemoX: '3' } }, tunables: { kDoorGen: 'True' } }, off: { ini: { RouteSnap: { DoorMemoX: '-1' } }, tunables: { kDoorGen: 'False' } } },
    { id: 'gc', status: 'verified', on: { ini: { GC: { Enabled: '1' } }, tunables: { kA: 'True' } } },
    { id: 'u', status: 'unverified', on: { tunables: { kB: 'True' } } },
  ] };
  const p = plan(f);
  assert.deepEqual(p, { ini: { RouteSnap: { DoorMemoX: '-1' }, GC: { Enabled: '1' } }, tunables: { kDoorGen: 'False', kA: 'True' } });
  const out = applyIni(ini, p.ini);
  assert.match(out, /\[RouteSnap\]\r\nEnabled=1\r\nDoorMemoX=-1\r\nDoorMemoXMs=250\r\n/);
  assert.throws(() => applyIni(ini, { RouteSnap: { DoorMemoXX: '1' } }), /found 0 times/);
  assert.throws(() => applyIni(ini, { GC: { DoorMemoX: '1' } }), /found 0 times/, 'key of another section');
  assert.throws(() => plan({ schema: 1, switches: [{ id: 'h', status: 'verified', on: { ini: { Experimental: { HouseholdToo: '1' } } } }] }), /HouseholdToo/);
  assert.throws(() => plan({ schema: 1, switches: [{ id: 'x', status: 'verified' }] }), /needs its on values/);
  assert.throws(() => plan({ schema: 1, switches: [{ id: 'a', status: 'verified', on: { tunables: { kA: 'True' } } }, { id: 'b', status: 'blocked', off: { tunables: { kA: 'False' } } }] }), /kA/);
  const xml = '<base>\n  <kA value="False" />\n  <kDoorGen value="True" />\n</base>';
  assert.equal(applyTunables(xml, p.tunables), '<base>\n  <kA value="True" />\n  <kDoorGen value="False" />\n</base>');
  assert.throws(() => applyTunables(xml, { kZ: 'True' }), /found 0 times/);
  assert.equal(nextTag('v0.2.12', Date.UTC(2026, 10, 17, 17, 0)), 'v0.2.13-exp.20261118', 'PHT date rolls at 16:00 UTC');
  assert.equal(nextTag('v0.2.12', Date.UTC(2026, 10, 17, 4, 0)), 'v0.2.13-exp.20261117');
  assert.throws(() => nextTag('v0.2.12-exp.20261117', 0));
  const r = (t: string, pre: boolean, d: string): Rel => ({ tagName: t, isPrerelease: pre, isDraft: false, createdAt: d });
  const rels = [r('v0.2.12', false, '2026-11-13'), r('v0.2.11', false, '2026-11-12'), r('v0.2.13-rc1', true, '2026-11-14'),
    ...Array.from({ length: 9 }, (_, i) => r(`v0.2.13-exp.202611${String(10 + i)}`, true, `2026-11-${10 + i}`))];
  assert.equal(newestStable(rels), 'v0.2.12');
  assert.deepEqual(toPrune(rels, 7), ['v0.2.13-exp.20261111', 'v0.2.13-exp.20261110']);
  assert.deepEqual(toPrune(rels, 20), []);
  assert.equal(expReleases(rels)[0].tagName, 'v0.2.13-exp.20261118');
  assert.deepEqual(readSource(`x\n${marker('1aefcef8f'.padEnd(40, '0'), 'abcdef0123456789')}\n`), { sha: '1aefcef8f'.padEnd(40, '0'), sw: 'abcdef0123456789' });
  assert.equal(readSource('no marker'), null);
  assert.equal(busy('hold:        -\njob:         -\ngame:        -\nchains:      -\nqueues:      -\nmain.lock:   -'), '');
  assert.match(busy('hold:        -\njob:         -\ngame:        TS3W.exe 1\nchains:      chain-a.sh pid 2 since x\nqueues:      -'), /^game TS3W\.exe 1; chains chain-a\.sh/);
  const home = ['C:', 'Users', 'som' + 'eone', 'x'].join('\\');
  assert.equal(personal('a', Buffer.from(home), false).length, 1, 'home path');
  assert.equal(personal('a', Buffer.from('mail: a.b' + '@example.com'), true).length, 1, 'email');
  assert.deepEqual(personal('a', Buffer.from('C:\\Users\\Public\\x [GC] Enabled=1'), true), []);
  const n = notes({ tag: 'v0.2.13-exp.20261117', stable: 'v0.2.12', repo: 'o/r', pubSha: 'f'.repeat(40), native: '0.36.1', ini: out, pkgSha: 'a', zipName: 'z.zip', zipSha: 'b', f: { ...f, knownIssues: ['k1'] }, src: 'c'.repeat(40), sw: 'd'.repeat(16) });
  assert.match(n, /\*\*dmx\*\*: `\[RouteSnap\] DoorMemoX=-1`, `kDoorGen=False`/);
  assert.match(n, /\*\*gc\*\*: `\[GC\] Enabled=1`, `kA=True`/);
  assert.doesNotMatch(n, /\*\*u\*\*/);
  assert.match(n, /- k1\n/);
  assert.deepEqual(readSource(n), { sha: 'c'.repeat(40), sw: 'd'.repeat(16) });
  console.log('experimental self-check ok');
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  try { main(); } catch (e: any) { log(`FAILED: ${e.message}`); process.exit(1); }
}
