// Build database: which ts3native.asi / ts3fixes.package builds exist, so every test can prove it ran the build it names.
//   node tools/builds.ts list
//   node tools/builds.ts add <file> --name <n> [--commit c] [--note ...]        record a parked build (asi or ts3fixes package)
//   node tools/builds.ts add - --kind ts3fixes --sha256 <hex> --commit <c> --name <n> [--note ...]
//        a package that is not on disk (e.g. a published asset): the hash as published, tuning from build-fixes.ts at <c>
//   node tools/builds.ts check <file> --expect <name>                            exit 1 when the file is not that build
//   node tools/builds.ts --self-check
// DB: docs/data/builds.json, one entry per line. asi entries are byte-identical builds (sha256 + the `ts3native X.Y.Z`
// version string in the binary). ts3fixes packages are NOT byte-reproducible: csc writes a new PE timestamp, MVID and
// <PrivateImplementationDetails>{guid} name plus a few dependent bytes on every build (2026-10-24: two builds of the same
// source differed in 47 bytes), so a package is identified by tuningDigest = sha256 of its tuning XML resource
// (0x0333406C, instance fnv64("Ts3Fixes.Instantiator"): the release defaults, what went wrong on 2026-10-24) and
// srcDigest = sha256 of `git ls-tree -r <commit> -- mods/ts3fixes/src` (the source blobs it was built from; provenance only,
// it cannot be read back from a package). queue.ts checks asi sha256 + version, and the installed package's tuningDigest.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { readIndex } from './extract-assemblies.ts';
import { fnv64 } from './dbpf.ts';

const ROOT = resolve(import.meta.dirname, '..');
export const DB_PATH = join(ROOT, 'docs', 'data', 'builds.json');
const TYPE_XML = 0x0333406c;
const TUNING_INST = fnv64('Ts3Fixes.Instantiator');

export type AsiBuild = { name: string; kind: 'asi'; sha256: string; version: string; commit?: string; created: string; note?: string };
export type FixesBuild = { name: string; kind: 'ts3fixes'; sha256: string; tuningDigest: string; srcDigest?: string; commit?: string; created: string; note?: string;
  tuning: Record<string, boolean | number | string> };
// IniBuild: a shipped ts3native.ini (sha only; release pairs name it, the queue build check does not read it)
export type IniBuild = { name: string; kind: 'ini'; sha256: string; created: string; note?: string };
export type Build = AsiBuild | FixesBuild | IniBuild;

export const sha256 = (b: Buffer | string) => createHash('sha256').update(b).digest('hex');
export function loadDb(path = DB_PATH): Build[] { return existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')).builds : []; }
export function saveDb(db: Build[], path = DB_PATH) { writeFileSync(path, `{ "builds": [\n${db.map(b => `  ${JSON.stringify(b)}`).join(',\n')}\n] }\n`); }

// the DLL holds the log line format "ts3native <version> %04d..." (queue.ts staleAsi reads the same string)
export const asiVersion = (b: Buffer) => /ts3native (\d+\.\d+\.\d+[a-z]?) %04d/.exec(b.toString('latin1'))?.[1];
// first line of Bin/ts3native.log: "ts3native 0.36.40 2026-10-24 16:41:59"
export const logVersion = (log: string) => /^ts3native (\S+) /.exec(log)?.[1];

export function tuningMap(xml: string): Record<string, boolean | number | string> {
  const out: Record<string, boolean | number | string> = {};
  for (const m of xml.matchAll(/<(k\w+) value="([^"]*)"/g)) out[m[1]] = m[2] === 'True' ? true : m[2] === 'False' ? false : /^-?\d+(\.\d+)?$/.test(m[2]) ? Number(m[2]) : m[2];
  return out;
}
export function packageTuning(pkg: Buffer): { digest: string; tuning: Record<string, boolean | number | string> } {
  const e = readIndex(pkg).find(x => x.type === TYPE_XML && x.inst === TUNING_INST);
  if (!e) throw new Error('no Ts3Fixes.Instantiator tuning XML in the package');
  if (e.fileSize !== e.memSize) throw new Error('compressed tuning resource (build-fixes.ts writes it uncompressed)');
  const xml = pkg.subarray(e.offset, e.offset + e.fileSize);
  return { digest: sha256(xml), tuning: tuningMap(xml.toString('utf8')) };
}
// the TUNING_XML literal of tools/setup/build-fixes.ts (build-fixes writes it as UTF-8 bytes, so its digest is the package's)
export const tuningXmlOf = (src: string) => { const m = /const TUNING_XML = `([^`]*)`;/.exec(src); if (!m) throw new Error('no TUNING_XML in build-fixes.ts'); return m[1]; };
const git = (...a: string[]) => execFileSync('git', ['-C', ROOT, ...a], { maxBuffer: 64 << 20 }).toString();
export const srcDigestAt = (commit: string) => sha256(git('ls-tree', '-r', commit, '--', 'mods/ts3fixes/src'));

// ---- checks used by queue.ts: '' = ok, else why
export function asiCheck(db: Build[], jobAsi: Buffer, jobAsiName: string, binAsi: Buffer): { fails: string[]; sha: string; names: string[]; version?: string } {
  const sha = sha256(jobAsi), fails: string[] = [];
  if (sha256(binAsi) !== sha) fails.push(`Bin/ts3native.asi (${sha256(binAsi).slice(0, 12)}) is not the job's asi ${jobAsiName} (${sha.slice(0, 12)})`);
  const named = db.find(b => b.kind === 'asi' && b.name === basename(jobAsiName));
  if (named && named.sha256 !== sha) fails.push(`${basename(jobAsiName)} is ${sha.slice(0, 12)}, the build DB has ${named.sha256.slice(0, 12)} under that name (parked build changed)`);
  const hits = db.filter((b): b is AsiBuild => b.kind === 'asi' && b.sha256 === sha), bin = asiVersion(jobAsi);
  for (const h of hits) if (h.version !== bin) fails.push(`DB ${h.name} says ts3native ${h.version}, the binary says ${bin ?? 'none'}`);
  const version = hits[0]?.version ?? bin;
  if (!version) fails.push(`no ts3native version string in ${jobAsiName}`);
  return { fails, sha, names: hits.map(h => h.name), version };
}
export function fixesCheck(db: Build[], installed: Buffer | null, expect: { name?: string; built?: Buffer | null }): { fails: string[]; sha?: string; digest?: string; names: string[]; expected?: string } {
  if (!installed) return { fails: ['Mods/Packages/ts3fixes.package missing'], names: [] };
  let got: { digest: string } | null = null;
  try { got = packageTuning(installed); } catch (e: any) { return { fails: [`installed ts3fixes.package: ${e.message}`], names: [], sha: sha256(installed) }; }
  const sha = sha256(installed), fails: string[] = [], names = db.filter(b => b.kind === 'ts3fixes' && b.tuningDigest === got!.digest).map(b => b.name);
  let expected: string | undefined;
  if (expect.name) {
    const e = db.find(b => b.name === expect.name);
    if (!e || e.kind !== 'ts3fixes') fails.push(`fixesBuild ${expect.name} is not a ts3fixes entry in docs/data/builds.json`);
    else if ((expected = e.tuningDigest) !== got.digest) fails.push(`installed ts3fixes.package tuning ${got.digest.slice(0, 12)} is not fixesBuild ${e.name} (${e.tuningDigest.slice(0, 12)})${diffTuning(e.tuning, packageTuning(installed).tuning)}`);
  } else {
    if (!expect.built) fails.push('build/ts3fixes.package missing (nothing to compare the installed package with)');
    else {
      expected = packageTuning(expect.built).digest;
      if (expected !== got.digest) fails.push(`installed ts3fixes.package tuning ${got.digest.slice(0, 12)} is not build/ts3fixes.package's ${expected.slice(0, 12)} (stale install)`);
      else if (sha256(expect.built) !== sha) fails.push(`installed ts3fixes.package (${sha.slice(0, 12)}) is not build/ts3fixes.package (${sha256(expect.built).slice(0, 12)}) byte for byte (stale code, same tuning)`);
    }
  }
  return { fails, sha, digest: got.digest, names, expected };
}
const diffTuning = (want: Record<string, unknown>, got: Record<string, unknown>) => {
  const d = [...new Set([...Object.keys(want), ...Object.keys(got)])].filter(k => want[k] !== got[k]).map(k => `${k} ${got[k] ?? 'absent'} (want ${want[k] ?? 'absent'})`);
  return d.length ? `: ${d.slice(0, 6).join(', ')}${d.length > 6 ? ` +${d.length - 6}` : ''}` : '';
};
export function logVersionCheck(log: string, want?: string): string {
  const v = logVersion(log);
  return v === want ? '' : `ts3native.log first line says ${v ? `ts3native ${v}` : 'no version'}, expected ${want ?? '?'} (the game did not load the expected asi)`;
}

// ---- CLI
if (process.argv[1]?.endsWith('builds.ts')) {
  const args = process.argv.slice(2);
  const opt = (k: string) => { const i = args.indexOf(`--${k}`); return i >= 0 ? args[i + 1] : undefined; };
  const db = loadDb();
  if (args.includes('--self-check')) {
    const assert = (await import('node:assert')).strict;
    const { writePackage } = await import('./dbpf.ts');
    const xml = tuningXmlOf(readFileSync(join(ROOT, 'tools', 'setup', 'build-fixes.ts'), 'utf8'));
    const pkg = (x: string) => writePackage([{ type: 0x073faa07, group: 0, instance: fnv64('Ts3Fixes'), data: Buffer.from('MZ asm') },
      { type: TYPE_XML, group: 0, instance: TUNING_INST, data: Buffer.from(x, 'utf8') }]);
    const p1 = pkg(xml), t1 = packageTuning(p1);
    assert.equal(t1.digest, sha256(Buffer.from(xml, 'utf8')), 'tuning digest = sha256 of the XML build-fixes.ts writes');
    assert.equal(t1.tuning.kInstantiator, true); assert.equal(typeof t1.tuning.kUnseenThrottleEvery, 'number');
    assert.deepEqual(tuningMap('<kA value="True" /><kB value="False" /><kC value="4" />'), { kA: true, kB: false, kC: 4 });
    const p2 = pkg(xml.replace('<kStuckCheckAsync value="True" />', '<kStuckCheckAsync value="False" />'));
    assert.notEqual(packageTuning(p2).digest, t1.digest);
    assert.throws(() => packageTuning(writePackage([])), /no Ts3Fixes/);
    // asi: version string, Bin vs job, parked-name drift, DB version
    const asi = Buffer.from('xx ts3native 0.36.37 %04d-%02d yy'), other = Buffer.from('ts3native 0.36.40 %04d');
    assert.equal(asiVersion(asi), '0.36.37'); assert.equal(asiVersion(Buffer.from('nothing')), undefined);
    const fdb: Build[] = [{ name: 'ts3native.asi.t1', kind: 'asi', sha256: sha256(asi), version: '0.36.37', created: '' },
      { name: 'fx', kind: 'ts3fixes', sha256: sha256(p1), tuningDigest: t1.digest, created: '', tuning: t1.tuning }];
    assert.deepEqual(asiCheck(fdb, asi, 'game-copy/ts3native.asi.t1', asi), { fails: [], sha: sha256(asi), names: ['ts3native.asi.t1'], version: '0.36.37' });
    assert.match(asiCheck(fdb, asi, 'x', other).fails[0], /is not the job's asi/);
    assert.match(asiCheck(fdb, other, 'game-copy/ts3native.asi.t1', other).fails[0], /parked build changed/);
    assert.match(asiCheck([{ ...fdb[0], version: '0.1.0' } as Build], asi, 'y', asi).fails[0], /DB ts3native.asi.t1 says ts3native 0.1.0/);
    assert.equal(asiCheck(fdb, other, 'z', other).version, '0.36.40'); // not in the DB: the binary's string
    assert.match(asiCheck(fdb, Buffer.from('no'), 'z', Buffer.from('no')).fails[0], /no ts3native version/);
    // fixes: by DB name and by build/ (stale install = the 2026-10-24 case)
    assert.deepEqual(fixesCheck(fdb, p1, { name: 'fx' }).fails, []);
    assert.deepEqual(fixesCheck(fdb, p1, { name: 'fx' }).names, ['fx']);
    assert.match(fixesCheck(fdb, p2, { name: 'fx' }).fails[0], /is not fixesBuild fx .*kStuckCheckAsync false \(want true\)/);
    assert.match(fixesCheck(fdb, p1, { name: 'nope' }).fails[0], /not a ts3fixes entry/);
    assert.deepEqual(fixesCheck(fdb, p1, { built: p1 }).fails, []);
    assert.match(fixesCheck(fdb, p2, { built: p1 }).fails[0], /stale install/);
    assert.match(fixesCheck(fdb, Buffer.concat([p1, Buffer.from('x')]), { built: p1 }).fails[0], /byte for byte/);
    assert.match(fixesCheck(fdb, null, {}).fails[0], /missing/);
    assert.match(fixesCheck(fdb, p1, { built: null }).fails[0], /build\/ts3fixes.package missing/);
    // log first line
    assert.equal(logVersionCheck('ts3native 0.36.37 2026-10-24 06:49:01\nhost ...', '0.36.37'), '');
    assert.match(logVersionCheck('ts3native 0.36.40 2026-10-24\n', '0.36.37'), /says ts3native 0.36.40, expected 0.36.37/);
    assert.match(logVersionCheck('', '0.36.37'), /no version/);
    // the committed DB: unique names, valid hashes, versions on every asi, digests on every package
    const names = new Set<string>();
    for (const b of db) {
      assert.ok(!names.has(b.name), `duplicate DB name ${b.name}`); names.add(b.name);
      assert.match(b.sha256, /^[0-9a-f]{64}$/, `${b.name}: sha256`);
      if (b.kind === 'asi') assert.match(b.version, /^\d+\.\d+\.\d+/, `${b.name}: version`);
      else if (b.kind === 'ts3fixes') { assert.match(b.tuningDigest, /^[0-9a-f]{64}$/, `${b.name}: tuningDigest`); assert.ok(Object.keys(b.tuning).length > 10, `${b.name}: tuning`); }
    }
    console.log(`builds self-check ok (${db.length} DB entries)`);
    process.exit(0);
  }
  const [cmd, file] = args;
  if (cmd === 'list') {
    for (const b of db) console.log(`${b.name.padEnd(36)} ${b.kind.padEnd(8)} ${b.sha256.slice(0, 12)} ${b.kind === 'asi' ? `ts3native ${b.version}` : `tuning ${b.tuningDigest.slice(0, 12)}`} ${b.commit ?? '-'} ${b.created.slice(0, 10)} ${b.note ?? ''}`);
    process.exit(0);
  }
  if (cmd === 'add' && file) {
    const name = opt('name'), commit = opt('commit'), note = opt('note');
    if (!name || !/^[\w.+-]+$/.test(name)) { console.error('add: --name <letters, digits, . _ + ->'); process.exit(2); }
    if (db.some(b => b.name === name)) { console.error(`add: ${name} is already in the DB`); process.exit(2); }
    let entry: Build;
    if (file === '-') { // not on disk: published hash, tuning from build-fixes.ts at the commit
      const sha = opt('sha256');
      if (opt('kind') !== 'ts3fixes' || !sha || !/^[0-9a-f]{64}$/.test(sha) || !commit) { console.error('add -: needs --kind ts3fixes --sha256 <64 hex> --commit <c>'); process.exit(2); }
      const xml = tuningXmlOf(git('show', `${commit}:tools/setup/build-fixes.ts`));
      entry = { name, kind: 'ts3fixes', sha256: sha, tuningDigest: sha256(Buffer.from(xml, 'utf8')), srcDigest: srcDigestAt(commit), commit, created: new Date().toISOString().slice(0, 10),
        note: note ?? `file not local; tuning from build-fixes.ts at ${commit}`, tuning: tuningMap(xml) };
    } else {
      const buf = readFileSync(file), created = statSync(file).mtime.toISOString().slice(0, 19);
      if (buf.toString('latin1', 0, 4) === 'DBPF') {
        const t = packageTuning(buf), c = commit ?? git('rev-parse', '--short=8', 'HEAD').trim();
        entry = { name, kind: 'ts3fixes', sha256: sha256(buf), tuningDigest: t.digest, srcDigest: srcDigestAt(c), commit: c, created, ...(note && { note }), tuning: t.tuning };
      } else {
        const version = asiVersion(buf);
        if (!version) { console.error(`add: ${file}: no ts3native version string (not a ts3native.asi?)`); process.exit(2); }
        entry = { name, kind: 'asi', sha256: sha256(buf), version, ...(commit && { commit }), created, ...(note && { note }) };
      }
    }
    const same = db.filter(b => b.sha256 === entry.sha256).map(b => b.name);
    if (same.length) console.log(`note: same file as ${same.join(', ')}`);
    db.push(entry); saveDb(db);
    console.log(`added ${name}: ${entry.kind} ${entry.sha256.slice(0, 12)} ${entry.kind === 'asi' ? entry.version : `tuning ${entry.tuningDigest.slice(0, 12)}`}`);
    process.exit(0);
  }
  if (cmd === 'check' && file && opt('expect')) {
    const want = db.find(b => b.name === opt('expect'));
    if (!want) { console.error(`check: no ${opt('expect')} in the DB`); process.exit(2); }
    const buf = readFileSync(file);
    const why = want.kind === 'asi'
      ? (sha256(buf) !== want.sha256 ? `sha256 ${sha256(buf).slice(0, 12)} is not ${want.name} (${want.sha256.slice(0, 12)})` : '')
      : fixesCheck(db, buf, { name: want.name }).fails.join('; ');
    console.log(why ? `MISMATCH: ${file}: ${why}` : `OK: ${file} is ${want.name}`);
    process.exit(why ? 1 : 0);
  }
  console.error('usage: node tools/builds.ts list | add <file|-> --name n [--commit c] [--note t] [--kind ts3fixes --sha256 h] | check <file> --expect <name> | --self-check');
  process.exit(2);
}
