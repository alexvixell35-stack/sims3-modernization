// Public sync + release (docs/RELEASE.md "Publishing"): automates the manual steps of the v0.1.5 release.
//   node tools/publish-public.ts sync --message-file <f> [--yes]
//        clone the public repo into a temp dir, new branch, replace the tree with `git archive <ref>` of this repo, apply
//        tools/public-strip.json (held-back files and hooks), scrub personal names, check-docs-code, secret scan of the staged diff, commit; with --yes: push, PR, watch checks,
//        squash-merge with --admin
//   node tools/publish-public.ts release v0.1.6 --native-version 0.36.40 --message-file <f> [--yes]
//        sync, then (with --yes) tag the merge commit, watch the tag's build.yml run, download the draft's ts3native zip and
//        verify it (asi version string, ini sha256 = the tested ini, enabled sections), check and attach the ts3fixes
//        package (four shortcuts True, release-features tunables), publish the draft as latest
//   node tools/publish-public.ts sync --if-behind [--min-gap-min 60] --message-file <f> --yes
//        automatic mode (queue.ts calls it after a run): does nothing (one "skip" line, exit 0) unless master has commits
//        newer than the marker probe-out/publish-public-last.json (sha + time, written after each successful --yes sync)
//        and the last sync is at least --min-gap-min old; otherwise the normal flow with all its safety stops
//   node tools/publish-public.ts --self-check     offline: scrub, secret scan, zip/package checks, sync --dry-run against a
//                                                  local temp bare repo (no network, nothing pushed)
// Options: --repo <owner/name> (rodneypantonial/sims3-modernization) --remote <clone url> (default https://github.com/<repo>.git)
//   --ref <private ref> (master) --branch <name> --message <m> | --message-file <f> (first line = commit summary + PR title,
//   rest = PR body) --author-name/--author-email (default: git config) --ini <tested ts3native.ini>
//   (native/ts3native/out/ts3native.ini) --package <ts3fixes.package> (build/ts3fixes.package)
// Default is a dry run: every step is printed, local steps run in the temp clone, and the script stops before the first
// network write (push, PR, merge, tag, upload, publish). --yes performs them. gh reads GH_TOKEN from the environment
// (personal account: GH_TOKEN="$PERSONAL_GITHUB_TOKEN"); the script never prints it.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync, copyFileSync } from 'node:fs';
import { join, resolve, relative } from 'node:path';
import { tmpdir } from 'node:os';
import { createHash } from 'node:crypto';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { unzip, zip } from './mts-zip.ts';
import { FEATURES, status } from './release-features.ts';

const ROOT = resolve(import.meta.dirname, '..');
// built from pieces so this file survives its own scrub
const SCRUB: [RegExp, string][] = [[new RegExp('rod' + 'ne\\b', 'gi'), '<user>'], [new RegExp('LAPTOP' + '-M15', 'gi'), '<pc>'], [new RegExp('ALRPan' + 'tonial1', 'gi'), '<pc2>'], [new RegExp('RPan' + 'tonial', 'gi'), '<user>'], // host before user: the host name contains the user name
  // any Windows home folder: back/forward/doubled slashes, 8.3 names (ABC~1); Public/Default stay, <user> and other <placeholders> never match
  [/(C:[\\/]+Users[\\/]+)(?!(?:Public|Default)(?:[^\w~.-]|$))[^\\/\s"'<>:*?|%$`{}()]+/gi, '$1<user>']];
const SECRETS: [string, RegExp][] = [
  ['GitHub token', /\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})/],
  ['Slack token', /\bxox[bp]-[A-Za-z0-9-]{10,}/],
  ['AWS access key', /\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/],
  ['private key', /-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----/],
];
const SHORTCUTS = ['GoHome', 'BuildingTrip', 'LotTrip', 'VirtualHome'];
export const MARKER = join(ROOT, 'probe-out', 'publish-public-last.json');
const sha256 = (b: Buffer | string) => createHash('sha256').update(b).digest('hex');

// --- pure checks (self-checked) ---

// poll ready() every intervalMs until it returns true, at most timeoutMs; a throw counts as not ready (gh errors right after
// the PR); returns the polls made, throws with the last error when the time runs out
export function waitUntil(ready: () => boolean, what: string, timeoutMs: number, intervalMs: number, sleepFn: (ms: number) => void, now = Date.now): number {
  const end = now() + timeoutMs;
  let last = '';
  for (let polls = 1; ; polls++) {
    try { if (ready()) return polls; last = ''; } catch (e: any) { last = String(e?.message ?? e).split('\n')[0]; }
    if (now() >= end) throw new Error(`${what}: not after ${Math.round(timeoutMs / 1000)} s${last ? ` (last error: ${last})` : ''}`);
    sleepFn(intervalMs);
  }
}

// --if-behind: sync only when master moved since the marker and the last sync is at least minGapMin old
export function behindDecision(master: string, marker: { sha: string; at: string } | null, nowMs: number, minGapMin: number): { go: boolean; reason: string } {
  if (!marker) return { go: true, reason: 'no sync marker yet: first automatic sync' };
  if (marker.sha === master) return { go: false, reason: `master ${master.slice(0, 8)} is already synced` };
  const ageMin = (nowMs - Date.parse(marker.at)) / 60000;
  if (Number.isFinite(ageMin) && ageMin < minGapMin) return { go: false, reason: `master moved, but the last sync was ${Math.round(ageMin)} min ago (< ${minGapMin} min)` };
  return { go: true, reason: `master ${master.slice(0, 8)} is ahead of the last sync ${marker.sha.slice(0, 8)}` };
}
export function readMarker(file = MARKER): { sha: string; at: string } | null {
  try { const m = JSON.parse(readFileSync(file, 'utf8')); return typeof m?.sha === 'string' && typeof m?.at === 'string' ? m : null; } catch { return null; }
}

// latin1 keeps every byte as is; the replacements are ASCII
export function scrub(text: string): string { return SCRUB.reduce((t, [re, to]) => t.replace(re, to), text); }
export const isText = (b: Buffer) => !b.subarray(0, 8000).includes(0);
export function leftovers(text: string): string[] { return SCRUB.filter(([re]) => new RegExp(re.source, re.flags.replace('g', '')).test(text)).map(([re]) => re.source); }

// added lines of a unified diff that look like secrets: "file: kind"
export function secretScan(diff: string): string[] {
  const hits: string[] = [];
  let file = '';
  for (const l of diff.split('\n')) {
    if (l.startsWith('+++ ')) { file = l.slice(6); continue; }
    if (!l.startsWith('+')) continue;
    for (const [kind, re] of SECRETS) if (re.test(l)) hits.push(`${file}: ${kind}`);
  }
  return hits;
}

// tools/public-strip.json (private: it lists itself in remove): what stays off the public repo even though master has it.
// { remove: [paths], edits: [{ file, replace: [[regex (gm), to]] }], banned: regex }. A rule that matches nothing or a banned
// hit afterwards throws, so a hook that moved on master stops the sync instead of going public. Returns the edited files.
export function publicStrip(clone: string): string[] {
  const p = join(clone, 'tools/public-strip.json');
  if (!existsSync(p)) return [];
  const s = JSON.parse(readFileSync(p, 'utf8')) as { remove: string[]; banned: string; edits: { file: string; replace: [string, string][] }[] };
  for (const e of s.edits) {
    const f = join(clone, e.file);
    let t = readFileSync(f, 'latin1');
    for (const [re, to] of e.replace) {
      const n = t.replace(new RegExp(re, 'gm'), to);
      if (n === t) throw new Error(`public-strip: ${e.file}: /${re}/ matched nothing`);
      t = n;
    }
    writeFileSync(f, t, 'latin1');
  }
  for (const r of s.remove) rmSync(join(clone, r), { recursive: true, force: true });
  const banned = new RegExp(s.banned), left = walk(clone).filter(f => banned.test(readFileSync(f, 'latin1'))).map(f => relative(clone, f));
  if (left.length) throw new Error(`public-strip: banned text left in:\n${left.join('\n')}`);
  return s.edits.map(e => e.file);
}

export function enabledSections(ini: string): string[] {
  let s = ''; const on: string[] = [];
  for (const raw of ini.split(/\r?\n/)) {
    const l = raw.trim();
    if (l.startsWith('[')) s = l;
    else { const m = /^Enabled\s*=\s*(\d+)/.exec(l); if (m && +m[1]) on.push(s); }
  }
  return on;
}

// the draft's ts3native-<tag>.zip: asi version, ini = tested ini; returns the enabled sections
export function verifyZip(z: Buffer, nativeVersion: string, testedIni: Buffer): string[] {
  const files = unzip(z);
  const asi = files.get('ts3native.asi'), ini = files.get('ts3native.ini');
  if (!asi || !ini) throw new Error(`zip holds ${[...files.keys()].join(', ')}, want ts3native.asi + ts3native.ini`);
  const versions = new Set([...asi.toString('latin1').matchAll(/ts3native (\d+\.\d+\.\d+[a-z]?)\b/g)].map(m => m[1]));
  if (versions.size !== 1 || !versions.has(nativeVersion)) throw new Error(`asi version ${[...versions].join(', ') || 'none'}, want ts3native ${nativeVersion}`);
  // CI zips a CRLF ini, the tested one is LF; the game reads it via GetPrivateProfile, so compare without CRs
  const noCr = (b: Buffer) => Buffer.from(b.toString('latin1').replace(/\r/g, ''), 'latin1');
  if (sha256(noCr(ini)) !== sha256(noCr(testedIni))) throw new Error(`zip ini sha256 ${sha256(noCr(ini))} != tested ini ${sha256(noCr(testedIni))} (CRs stripped)`);
  return enabledSections(ini.toString('latin1'));
}

// ts3fixes.package: four shortcuts True, every release-features tunable at the value the repo defaults ship
export function verifyPackage(pkg: Buffer, ini: string, tuning: string): string[] {
  const s = pkg.toString('latin1'), bad: string[] = [];
  for (const k of SHORTCUTS) if (!s.includes(`<k${k}Shortcut value="True"`)) bad.push(`k${k}Shortcut not True`);
  const on = new Set(status(ini, tuning));
  for (const [id, f] of Object.entries(FEATURES)) for (const t of f.tuning ?? []) {
    const want = on.has(id) ? 'True' : 'False';
    if (!s.includes(`<${t} value="${want}"`)) bad.push(`${t} not ${want} (release-features ${id})`);
  }
  return bad;
}

// the build.yml run of this tag push: same head SHA, created after the push (allowing 60 s clock skew), newest first. A
// re-tag (failed run, tag moved) leaves the old run under the same tag name; picking `.[0]` of the list watched that one.
export type TagRun = { databaseId: number; headSha: string; createdAt: string };
export function pickTagRun(runs: TagRun[], sha: string, pushedAtMs: number): number | undefined {
  return runs.filter(r => r.headSha === sha && Date.parse(r.createdAt) >= pushedAtMs - 60_000)
    .sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))[0]?.databaseId;
}

// --- runner ---

type Opts = { yes: boolean; repo: string; remote: string; src: string; ref: string; branch: string; message: string;
  authorName: string; authorEmail: string; tag?: string; nativeVersion?: string; ini: string; pkg: string; quiet?: boolean };

// Every child gets a timeout and no prompts (a credential helper waiting for input hung the queue-end sync 3x on 2026-11-09).
// Network git/gh: 120 s, local git/tar: 30 s, node check scripts: 600 s. Auth for git over https comes from the env token as an
// http extraheader passed through GIT_CONFIG_* (never on a command line, never logged); without a token the gh credential helper stays.
const token = () => process.env.GH_TOKEN || process.env.PERSONAL_GITHUB_TOKEN || '';
export function childEnv(base: NodeJS.ProcessEnv, tok: string): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = { ...base, GIT_TERMINAL_PROMPT: '0', GCM_INTERACTIVE: 'never', GH_PROMPT_DISABLED: '1' };
  if (tok) {
    env.GH_TOKEN = tok;
    Object.assign(env, { GIT_CONFIG_COUNT: '2', GIT_CONFIG_KEY_0: 'credential.helper', GIT_CONFIG_VALUE_0: '',
      GIT_CONFIG_KEY_1: 'http.https://github.com/.extraheader', GIT_CONFIG_VALUE_1: `AUTHORIZATION: basic ${Buffer.from(`x-access-token:${tok}`).toString('base64')}` });
  }
  return env;
}
export function timeoutFor(cmd: string, args: string[]): number {
  if (cmd === 'gh' || (cmd === 'git' && args.some(x => ['clone', 'fetch', 'push', 'pull'].includes(x)))) return 120_000;
  if (cmd === process.execPath) return 600_000;
  return 30_000;
}
const BIG = { maxBuffer: 1 << 30 };
function run(cmd: string, args: string[], cwd?: string, input?: Buffer | string): string {
  const timeout = timeoutFor(cmd, args);
  try {
    return execFileSync(cmd, args, { cwd, input, ...BIG, timeout, killSignal: 'SIGKILL', env: childEnv(process.env, token()),
      stdio: [input === undefined ? 'ignore' : 'pipe', 'pipe', 'pipe'] }).toString().trim();
  } catch (e: any) {
    if (e?.code === 'ETIMEDOUT') {
      const what = `${cmd === 'git' ? 'git ' + (args.find(x => /^[a-z]+$/.test(x) && !['core', 'credential'].includes(x)) ?? '') : cmd}`.trim();
      throw new Error(`${what} timed out after ${timeout / 1000} s; public sync skipped, run it by hand: node tools/publish-public.ts sync --yes --message-file <file> (check gh auth status / GH_TOKEN)`);
    }
    throw e;
  }
}
const git = (cwd: string, ...a: string[]) => run('git', ['-c', 'core.autocrlf=false', ...a], cwd);
const sleep = (ms: number) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);

function walk(dir: string, out: string[] = []): string[] {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    if (e.name === '.git') continue;
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p, out); else if (e.isFile()) out.push(p);
  }
  return out;
}

export function publish(o: Opts): { clone: string; commit: string } {
  const log = (s: string) => { if (!o.quiet) console.log(s); };
  let n = 0, stopped = false;
  const step = (s: string) => log(`${++n}. ${s}`);
  // a network write (or anything after one): runs only with --yes; a dry run prints it and every later step
  const remote = <T>(s: string, f: () => T, placeholder: T): T => {
    step(s);
    if (o.yes) return f();
    if (!stopped) log('   [dry run] stopped here: the steps below need --yes'); stopped = true;
    return placeholder;
  };

  // checks that need no network first, so a release fails before anything is written
  const testedIni = o.tag ? readFileSync(o.ini) : undefined;
  if (o.tag) {
    if (!/^v\d+\.\d+\.\d+$/.test(o.tag)) throw new Error(`tag ${o.tag}: want vX.Y.Z`);
    if (!o.nativeVersion) throw new Error('release needs --native-version X.Y.Z (the ts3native build that was tested)');
    step(`tested ini ${o.ini}: sha256 ${sha256(testedIni!)}, enabled ${enabledSections(testedIni!.toString('latin1')).join(' ')}`);
  }
  if (o.yes) {
    const login = run('gh', ['api', 'user', '-q', '.login']);
    if (login !== o.repo.split('/')[0]) throw new Error(`gh is logged in as ${login}, not ${o.repo.split('/')[0]}: set GH_TOKEN to the personal token`);
    step(`gh account ${login}`);
  }

  const tmp = mkdtempSync(join(tmpdir(), 'ts3-publish-')), clone = join(tmp, 'pub');
  step(`clone ${o.remote} -> ${clone}`);
  run('git', ['clone', '-q', '-c', 'core.autocrlf=false', ...(token() ? [] : ['-c', 'credential.helper=', '-c', 'credential.helper=!gh auth git-credential']), o.remote, clone]);
  step(`branch ${o.branch}`);
  git(clone, 'switch', '-q', '-c', o.branch);
  step('empty the tree (git rm -rq . + clean)');
  if (git(clone, 'ls-files')) git(clone, 'rm', '-rq', '.');
  git(clone, 'clean', '-fdxq');
  step(`git archive ${o.ref} of ${o.src} | tar -x`);
  git(o.src, 'archive', '--format=tar', '-o', join(tmp, 'src.tar'), o.ref);
  run('tar', ['-xf', '../src.tar'], clone);  // relative: GNU tar reads "C:" as a remote host
  rmSync(join(tmp, 'src.tar'));

  step('public-strip.json: held-back files and hooks');
  const stripped = publicStrip(clone);
  log(`   ${stripped.length} files edited`);

  step('scrub personal names');
  let changed = 0; const left: string[] = [];
  for (const f of walk(clone)) {
    const b = readFileSync(f), t = b.toString('latin1');
    if (isText(b)) { const s = scrub(t); if (s !== t) { writeFileSync(f, s, 'latin1'); changed++; } }
    for (const p of leftovers(isText(b) ? scrub(t) : t)) left.push(`${relative(clone, f)}: ${p}`);
  }
  log(`   ${changed} files scrubbed`);
  if (left.length) throw new Error(`still found after scrub (binary files are not scrubbed):\n${left.join('\n')}`);

  step('node tools/check-docs-code.ts');
  run(process.execPath, ['tools/check-docs-code.ts'], clone);
  if (stripped.length) {
    step('selftest-all --ci of tools/harness in the stripped tree');
    run(process.execPath, ['tools/selftest-all.ts', '--ci', '--only', 'tools/harness/*.ts'], clone);
  }

  step('stage + secret scan of the staged diff');
  git(clone, 'add', '-A');
  const stat = git(clone, 'diff', '--cached', '--shortstat');
  if (!stat) { log('   nothing to sync: the public repo already matches'); return { clone, commit: '' }; }
  log(`   ${stat}`);
  const secrets = secretScan(git(clone, 'diff', '--cached', '-U0', '--no-color', '--no-ext-diff'));
  if (secrets.length) throw new Error(`possible secrets in the staged diff:\n${secrets.join('\n')}`);

  step(`commit as ${o.authorName} <${o.authorEmail}>`);
  writeFileSync(join(tmp, 'msg.txt'), o.message);
  git(clone, '-c', `user.name=${o.authorName}`, '-c', `user.email=${o.authorEmail}`, 'commit', '-q', '-F', join(tmp, 'msg.txt'));
  const commit = git(clone, 'rev-parse', 'HEAD');
  log(`   ${commit} ${o.message.split('\n')[0]}`);

  if (o.tag) {
    step(`verify ${o.pkg} tuning (shortcuts, release-features tunables)`);
    const bad = verifyPackage(readFileSync(o.pkg), readFileSync(join(clone, 'native/ts3native/ts3native.ini'), 'latin1'), readFileSync(join(clone, 'tools/setup/build-fixes.ts'), 'latin1'));
    if (bad.length) throw new Error(`package: ${bad.join('; ')}`);
    log(`   ok, sha256 ${sha256(readFileSync(o.pkg))}`);
  }
  const R = ['--repo', o.repo];
  remote(`push ${o.branch}`, () => git(clone, 'push', '-q', '-u', 'origin', o.branch), '');
  const [title, ...body] = o.message.split('\n');
  const pr = remote('gh pr create', () => run('gh', ['pr', 'create', ...R, '--head', o.branch, '--title', title, '--body', body.join('\n').trim() || title]), '<pr url>');
  remote(`gh pr checks ${pr} --watch`, () => {
    // checks show up seconds to minutes after the PR, and `gh pr checks` exits 1 ("no checks reported") until then, the same
    // code as a failed check: wait for the rollup to list any, then watch
    waitUntil(() => Number(run('gh', ['pr', 'view', pr, ...R, '--json', 'statusCheckRollup', '-q', '.statusCheckRollup | length'])) > 0,
      `checks on ${pr}`, 180_000, 10_000, sleep);
    try { execFileSync('gh', ['pr', 'checks', pr, ...R, '--watch', '--interval', '20'], { stdio: 'inherit', timeout: 45 * 60_000, env: childEnv(process.env, token()) }); }
    catch { throw new Error(`gh pr checks ${pr}: a check failed`); }
  }, undefined);
  remote(`gh pr merge ${pr} --squash --admin`, () => run('gh', ['pr', 'merge', pr, ...R, '--squash', '--admin']), '');
  const sha = remote('merge commit', () => run('gh', ['pr', 'view', pr, ...R, '--json', 'mergeCommit', '-q', '.mergeCommit.oid']), '<merge sha>');
  if (o.yes) log(`   merged as ${sha}`);
  if (!o.tag) return { clone, commit };

  const tag = o.tag;
  const pushedAt = Date.now();
  remote(`tag ${tag} on ${sha}`, () => run('gh', ['api', `repos/${o.repo}/git/refs`, '-f', `ref=refs/tags/${tag}`, '-f', `sha=${sha}`]), '');
  remote(`watch the ${tag} build.yml run for ${sha}`, () => {
    for (let i = 0; ; i++) {
      const runs = JSON.parse(run('gh', ['run', 'list', ...R, '--workflow', 'build.yml', '--branch', tag, '--json', 'databaseId,headSha,createdAt']) || '[]');
      const id = pickTagRun(runs, sha, pushedAt);
      if (id) { execFileSync('gh', ['run', 'watch', String(id), ...R, '--exit-status', '--interval', '30'], { stdio: 'inherit', timeout: 45 * 60_000, env: childEnv(process.env, token()) }); return; }
      if (i >= 18) throw new Error(`no build.yml run for ${tag} at ${sha} after 3 min (older runs of the tag name are skipped)`);
      sleep(10_000);
    }
  }, undefined);
  remote(`download ts3native-${tag}.zip from the draft and verify (ts3native ${o.nativeVersion}, ini sha256, sections)`, () => {
    const dl = join(tmp, 'dl'); mkdirSync(dl);
    run('gh', ['release', 'download', tag, ...R, '--pattern', `ts3native-${tag}.zip`, '--dir', dl]);
    log(`   ok, enabled: ${verifyZip(readFileSync(join(dl, `ts3native-${tag}.zip`)), o.nativeVersion!, testedIni!).join(' ')}`);
  }, undefined);
  remote(`gh release upload ${tag} ts3fixes.package`, () => run('gh', ['release', 'upload', tag, o.pkg, ...R]), '');
  remote(`gh release edit ${tag} --draft=false --latest`, () => run('gh', ['release', 'edit', tag, ...R, '--draft=false', '--latest']), '');
  return { clone, commit };
}

// --- self-check (offline) ---

function selfCheck(): void {
  { // non-interactive env + timeouts
    const e = childEnv({ A: '1' }, 'tok');
    assert.equal(e.GIT_TERMINAL_PROMPT, '0'); assert.equal(e.GCM_INTERACTIVE, 'never'); assert.equal(e.GIT_CONFIG_VALUE_0, '');
    assert.match(e.GIT_CONFIG_VALUE_1!, /^AUTHORIZATION: basic /); assert.equal(childEnv({}, '').GIT_CONFIG_COUNT, undefined);
    assert.equal(timeoutFor('git', ['clone', 'x']), 120_000); assert.equal(timeoutFor('git', ['status']), 30_000); assert.equal(timeoutFor('gh', ['api']), 120_000);
    assert.throws(() => execFileSync(process.execPath, ['-e', 'setTimeout(()=>{},9e4)'], { timeout: 300, killSignal: 'SIGKILL' }), (x: any) => x.code === 'ETIMEDOUT');
  }
  const user = 'rod' + 'ne', pc = 'LAPTOP' + '-M15', pc2 = 'ALRPan' + 'tonial1', user2 = 'RPan' + 'tonial';
  assert.equal(scrub(`C:\\Users\\${user}\\x ${pc} ${user}y`), `C:\\Users\\<user>\\x <pc> ${user}y`, 'scrub');
  assert.equal(scrub(`ScriptError_${pc2}__11-02.xml ${pc2}`), 'ScriptError_<pc2>__11-02.xml <pc2>', 'scrub second host');
  assert.equal(scrub(`xcpt ${pc.toUpperCase()} 26-11-02.mdmp ${pc2.toUpperCase()} ${user.toUpperCase()}`), 'xcpt <pc> 26-11-02.mdmp <pc2> <user>', 'scrub upper-case names');
  assert.equal(leftovers(pc.toLowerCase()).length, 1, 'lower-case host is a leftover');
  assert.deepEqual(leftovers(scrub(`/home/${user}/ ${pc} ${pc2}`)), [], 'nothing left after scrub');
  assert.equal(leftovers(pc2).length, 2, 'second host is a leftover (host pattern and the user name inside it)');
  assert.equal(scrub(`C:\\Users\\${user2}\\x ${pc2}`), 'C:\\Users\\<user>\\x <pc2>', 'scrub second user; host first');
  assert.deepEqual(leftovers(scrub(user2)), [], 'second user scrubbed');
  { // any C:\Users\<name>; synthetic names only
    const B = String.fromCharCode(92), L = 'C:' + B + 'Users' + B, n = 'Jd' + 'oe', D = 'C:' + B + B + 'Users' + B + B; // pieces: the file survives its own scrub
    assert.equal(scrub(`${L}${n}${B}x`), `${L}<user>${B}x`, 'back-slash home');
    assert.equal(scrub(`c:/users/${n}/x C:/Users/${n}`), 'c:/users/<user>/x C:/Users/<user>', 'forward-slash home');
    assert.equal(scrub(`"${D}${n}${B}${B}x"`), `"${D}<user>${B}${B}x"`, 'doubled-backslash home');
    assert.equal(scrub(`${L}AB${'C'}~1${B}x`), `${L}<user>${B}x`, '8.3 short name');
    const keep = `${L}Public${B}x ${L}Public ${L}<user>${B}x C:/Users/Default/x`;
    assert.equal(scrub(keep), keep, 'Public, Default and scrubbed paths stay');
    assert.equal(scrub(`${L}Public${n}${B}x`), `${L}<user>${B}x`, 'name that starts with Public');
    assert.equal(leftovers(`${L}${n}`).length, 1, 'real-looking home is a leftover');
    assert.deepEqual(leftovers(keep), [], 'Public and scrubbed paths are not leftovers');
    assert.deepEqual(leftovers(scrub(`${L}${n}${B}x C:/Users/${n}~1`)), [], 'nothing left after home scrub');
  }
  { // waitUntil on a fake clock: checks appear on poll 4 after two gh errors; never = throws at the timeout with the last error
    let t = 0, n = 0;
    const clock = () => t, tick = (ms: number) => { t += ms; };
    const seq = [() => { throw new Error('no checks reported\nmore'); }, () => { throw new Error('x'); }, () => false, () => true];
    assert.equal(waitUntil(() => seq[n++](), 'c', 180_000, 10_000, tick, clock), 4, 'ready on poll 4');
    assert.equal(t, 30_000, 'slept 3 intervals');
    t = 0; n = 0;
    assert.throws(() => waitUntil(() => { n++; throw new Error('gh: no checks reported'); }, 'checks on #1', 180_000, 10_000, tick, clock),
      /checks on #1: not after 180 s \(last error: gh: no checks reported\)/, 'timeout names the last error');
    assert.equal(n, 19, '19 polls in 3 min at 10 s');
  }
  { // re-tag: the failed run of the old tag commit is listed first; the new commit's run is picked, none before it exists
    const push = Date.parse('2026-10-29T10:00:00Z');
    const old = { databaseId: 1, headSha: 'aaa', createdAt: '2026-10-29T08:00:00Z' };
    const rerunOld = { databaseId: 2, headSha: 'bbb', createdAt: '2026-10-29T09:00:00Z' };  // same SHA, before this push
    const cur = { databaseId: 3, headSha: 'bbb', createdAt: '2026-10-29T10:00:20Z' };
    assert.equal(pickTagRun([old], 'bbb', push), undefined, 'old tag run not picked');
    assert.equal(pickTagRun([old, rerunOld], 'bbb', push), undefined, 'run of the same SHA before the push not picked');
    assert.equal(pickTagRun([old, rerunOld, cur], 'bbb', push), 3, 'current run picked');
    assert.equal(pickTagRun([cur, old], 'bbb', push + 30_000), 3, 'clock skew tolerated');
  }
  { // public-strip.json: edit, remove (itself too), a stale rule and a banned hit stop the sync, no list = nothing to do
    const d = mkdtempSync(join(tmpdir(), 'ts3-strip-check-'));
    try {
      const put = (cfg: object) => { writeFileSync(join(d, 'a.ts'), 'keep\nhook();\n'); writeFileSync(join(d, 'b.ts'), 'x'); mkdirSync(join(d, 'tools'), { recursive: true }); writeFileSync(join(d, 'tools/public-strip.json'), JSON.stringify(cfg)); };
      const cfg = { remove: ['b.ts', 'tools/public-strip.json'], banned: 'hook', edits: [{ file: 'a.ts', replace: [['^hook.*\n', '']] }] };
      assert.deepEqual(publicStrip(d), [], 'no list');
      put(cfg);
      assert.deepEqual(publicStrip(d), ['a.ts'], 'edited files');
      assert.equal(readFileSync(join(d, 'a.ts'), 'utf8'), 'keep\n', 'hook line gone');
      assert.ok(!existsSync(join(d, 'b.ts')) && !existsSync(join(d, 'tools/public-strip.json')), 'files removed');
      put({ ...cfg, edits: [{ file: 'a.ts', replace: [['^gone', '']] }] });
      assert.throws(() => publicStrip(d), /matched nothing/, 'stale rule refused');
      put({ ...cfg, edits: [] });
      assert.throws(() => publicStrip(d), /banned text left in:\s+a\.ts/, 'banned hit refused');
    } finally { rmSync(d, { recursive: true, force: true }); }
  }
  assert.equal(leftovers(`${pc}`).length, 1, 'leftover found');
  assert.ok(!isText(Buffer.from([65, 0, 66])) && isText(Buffer.from('abc')), 'binary detection');

  const fake = ['gh' + 'p_' + 'a'.repeat(36), 'xo' + 'xb-' + '1234567890ab', 'AK' + 'IA' + 'ABCDEFGHIJKLMNOP', '-----BEGIN RSA ' + 'PRIVATE KEY-----'];
  for (const f of fake) assert.equal(secretScan(`+++ b/x.txt\n+${f}\n`).length, 1, `secret found: ${f.slice(0, 4)}`);
  assert.deepEqual(secretScan(`+++ b/x.txt\n-${fake[0]}\n+harmless ghp_ mention\n`), [], 'removed lines and plain mentions pass');
  assert.deepEqual(secretScan(readFileSync(import.meta.filename, 'utf8').split('\n').map(l => '+' + l).join('\n')), [], 'this file passes its own scan');

  const ini = Buffer.from('[GC]\nEnabled=1\n[RouteSnap]\nEnabled=0\n[RouteAsync]\r\nEnabled=1\r\n');
  const asi = (v: string) => Buffer.concat([Buffer.from('MZ\0\0'), Buffer.from(`ts3native crash report 1\0ts3native ${v}\r\n\0`)]);
  const z = (v: string, i = ini) => zip([['ts3native.asi', asi(v)], ['ts3native.ini', i]]);
  assert.deepEqual(verifyZip(z('0.36.40'), '0.36.40', ini), ['[GC]', '[RouteAsync]'], 'zip ok');
  assert.throws(() => verifyZip(z('0.36.39'), '0.36.40', ini), /asi version 0.36.39/, 'wrong version refused');
  assert.deepEqual(verifyZip(z('0.36.37a'), '0.36.37a', ini), ['[GC]', '[RouteAsync]'], 'hotfix letter suffix ok');
  assert.throws(() => verifyZip(z('0.36.37a'), '0.36.37', ini), /asi version 0.36.37a/, 'hotfix asi is not the base release');
  assert.throws(() => verifyZip(z('0.36.40', Buffer.from('[GC]\nEnabled=0\n')), '0.36.40', ini), /ini sha256/, 'other ini refused');
  assert.deepEqual(verifyZip(z('0.36.40', Buffer.from(ini.toString('latin1').replace(/\r?\n/g, '\r\n'), 'latin1')), '0.36.40', ini), ['[GC]', '[RouteAsync]'], 'CRLF ini accepted');

  const tunables = Object.values(FEATURES).flatMap(f => f.tuning ?? []);
  const repoIni = readFileSync(join(ROOT, 'native/ts3native/ts3native.ini'), 'latin1'), repoTun = readFileSync(join(ROOT, 'tools/setup/build-fixes.ts'), 'latin1');
  const on = new Set(status(repoIni, repoTun).flatMap(id => FEATURES[id].tuning ?? []));
  const pkg = (shortcut: string, flip = '') => Buffer.from('DBPF' + SHORTCUTS.map(k => `<k${k}Shortcut value="${shortcut}" />`).join('') +
    tunables.map(t => `<${t} value="${on.has(t) !== (t === flip) ? 'True' : 'False'}" />`).join(''));
  assert.deepEqual(verifyPackage(pkg('True'), repoIni, repoTun), [], 'package ok');
  assert.equal(verifyPackage(pkg('False'), repoIni, repoTun).length, 4, 'shortcuts off refused');
  assert.match(verifyPackage(pkg('True', tunables[0]), repoIni, repoTun).join(), new RegExp(tunables[0]), 'wrong tunable refused');

  // sync --dry-run end to end: private = a small temp repo, public = a local bare repo; nothing leaves this machine
  const tmp = mkdtempSync(join(tmpdir(), 'ts3-publish-check-'));
  try {
    const src = join(tmp, 'src'), bare = join(tmp, 'pub.git'), seed = join(tmp, 'seed');
    const g = (cwd: string, ...a: string[]) => run('git', ['-c', 'user.name=t', '-c', 'user.email=t@example.com', '-c', 'core.autocrlf=false', ...a], cwd);
    run('git', ['init', '-q', '--bare', '-b', 'main', bare]);
    run('git', ['clone', '-q', bare, seed]);
    writeFileSync(join(seed, 'OLD.md'), 'removed by the sync\n');
    g(seed, 'add', '-A'); g(seed, 'commit', '-qm', 'seed'); g(seed, 'push', '-q', 'origin', 'HEAD:main');
    mkdirSync(join(src, 'tools'), { recursive: true }); mkdirSync(join(src, 'docs'));
    run('git', ['init', '-q', '-b', 'master', src]);
    copyFileSync(join(ROOT, 'tools/check-docs-code.ts'), join(src, 'tools/check-docs-code.ts'));
    writeFileSync(join(src, 'docs/a.md'), `log in C:\\Users\\${user}\\Documents on ${pc}\n`);
    writeFileSync(join(src, 'bin.dat'), Buffer.from([1, 0, 2]));
    g(src, 'add', '-A'); g(src, 'commit', '-qm', 'one');
    const head = () => run('git', ['--git-dir', bare, 'rev-parse', 'main']);
    const before = head();
    const o: Opts = { yes: false, repo: 'example/public', remote: bare, src, ref: 'master', branch: 'sync/test', message: 'Sync\n\nbody',
      authorName: 't', authorEmail: 't@example.com', ini: '', pkg: '', quiet: true };
    const r = publish(o);
    assert.equal(readFileSync(join(r.clone, 'docs/a.md'), 'utf8'), 'log in C:\\Users\\<user>\\Documents on <pc>\n', 'clone scrubbed');
    assert.ok(!existsSync(join(r.clone, 'OLD.md')), 'old tree removed');
    assert.equal(run('git', ['show', '-s', '--format=%an <%ae>|%s'], r.clone), 't <t@example.com>|Sync', 'commit author + summary');
    assert.equal(head(), before, 'dry run pushed nothing');
    assert.equal(run('git', ['--git-dir', bare, 'branch', '--list', 'sync/test']), '', 'no branch on the remote');
    // a secret in the source stops the sync before the commit
    writeFileSync(join(src, 'docs/b.md'), `token ${fake[2]}\n`); g(src, 'add', '-A'); g(src, 'commit', '-qm', 'two');
    assert.throws(() => publish({ ...o, branch: 'sync/test2' }), /possible secrets[\s\S]*docs\/b.md: AWS access key/, 'secret stops the sync');
    // a personal name in a binary file cannot be scrubbed: stop
    g(src, 'rm', '-q', 'docs/b.md'); writeFileSync(join(src, 'bin.dat'), Buffer.concat([Buffer.from([0]), Buffer.from(pc)])); g(src, 'add', '-A'); g(src, 'commit', '-qm', 'three');
    assert.throws(() => publish({ ...o, branch: 'sync/test3' }), /still found after scrub[\s\S]*bin.dat/, 'binary leftover stops the sync');
    assert.equal(head(), before, 'still nothing pushed');
  } finally { rmSync(tmp, { recursive: true, force: true }); }
  { // --if-behind decision
    const m = { sha: 'a'.repeat(40), at: '2026-11-08T10:00:00Z' }, t = (min: number) => Date.parse(m.at) + min * 60000, b = 'b'.repeat(40);
    assert.equal(behindDecision(b, null, t(0), 60).go, true, 'no marker: sync');
    assert.equal(behindDecision(m.sha, m, t(500), 60).go, false, 'same sha: nothing to do');
    assert.match(behindDecision(m.sha, m, t(500), 60).reason, /already synced/);
    assert.equal(behindDecision(b, m, t(59), 60).go, false, 'behind but inside the gap');
    assert.match(behindDecision(b, m, t(59), 60).reason, /< 60 min/);
    assert.equal(behindDecision(b, m, t(60), 60).go, true, 'behind and gap reached');
    assert.equal(readMarker(join(tmpdir(), 'no-such-marker.json')), null);
  }
  console.log('publish-public self-check ok');
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const { values: a, positionals: p } = parseArgs({ allowPositionals: true, options: {
    yes: { type: 'boolean' }, 'dry-run': { type: 'boolean' }, 'self-check': { type: 'boolean' },
    repo: { type: 'string', default: 'rodneypantonial/sims3-modernization' }, remote: { type: 'string' }, ref: { type: 'string', default: 'master' },
    branch: { type: 'string' }, message: { type: 'string' }, 'message-file': { type: 'string' },
    'if-behind': { type: 'boolean' }, 'min-gap-min': { type: 'string', default: '60' },
    'author-name': { type: 'string' }, 'author-email': { type: 'string' }, 'native-version': { type: 'string' },
    ini: { type: 'string', default: join(ROOT, 'native/ts3native/out/ts3native.ini') }, package: { type: 'string', default: join(ROOT, 'build/ts3fixes.package') },
  } });
  try {
    if (a['self-check']) selfCheck();
    else if (p[0] === 'sync' || (p[0] === 'release' && p[1])) {
      if (a.yes && a['dry-run']) throw new Error('--yes and --dry-run together');
      if (a['if-behind'] && p[0] !== 'sync') throw new Error('--if-behind is for sync only');
      const synced = a['if-behind'] || a.yes ? run('git', ['rev-parse', a.ref!], ROOT) : ''; // read before the sync starts: a later commit is not marked synced
      if (a['if-behind']) {
        const d = behindDecision(synced, readMarker(), Date.now(), Number(a['min-gap-min']));
        console.log(`publish-public: ${d.go ? 'syncing' : 'skip'}: ${d.reason}`);
        if (!d.go) process.exit(0);
      }
      const message = a['message-file'] ? readFileSync(a['message-file'], 'utf8').trim() : a.message;
      if (!message) throw new Error('--message or --message-file is required (first line = commit summary and PR title)');
      const cfg = (k: string) => { try { return run('git', ['config', k], ROOT); } catch { return ''; } };
      const authorName = a['author-name'] ?? cfg('user.name'), authorEmail = a['author-email'] ?? cfg('user.email');
      if (!authorName || !authorEmail) throw new Error('no author: pass --author-name/--author-email or set git config');
      const tag = p[0] === 'release' ? p[1] : undefined;
      const r = publish({ yes: !!a.yes, repo: a.repo!, remote: a.remote ?? `https://github.com/${a.repo}.git`, src: ROOT, ref: a.ref!,
        branch: a.branch ?? (tag ? `release/${tag}` : `sync/${new Date().toISOString().slice(0, 16).replace(/[-:]/g, '').replace('T', '-')}`), message, authorName, authorEmail,
        tag, nativeVersion: a['native-version'], ini: a.ini!, pkg: a.package! });
      console.log(a.yes ? 'done' : `dry run done; the clone stays for review: ${r.clone}`);
      if (a.yes) {
        rmSync(resolve(r.clone, '..'), { recursive: true, force: true });
        if (!tag) { mkdirSync(resolve(MARKER, '..'), { recursive: true }); writeFileSync(MARKER, JSON.stringify({ sha: synced, at: new Date().toISOString() }) + '\n'); }
      }
    } else {
      console.error('usage: node tools/publish-public.ts sync|release <vX.Y.Z> --message-file <f> [--native-version X.Y.Z] [--yes] | --self-check');
      process.exit(2);
    }
  } catch (e: any) {
    console.error(`publish-public: ${e.message}${e.stderr?.length ? `\n${e.stderr.toString().trim()}` : ''}`);
    process.exit(1);
  }
}
