// queue.ts job key extraAsi (another ASI module in Bin for one job) and the on-disk backup of a job's Default.ini change.
// Both leave a record on disk BEFORE the game folder is touched, so a queue that is killed mid-job is cleaned up by the
// next queue start, by the queue's exit handler (game closed) and by the Bin restore of tools/turbo-compat.ts:
//   Bin/extra-asi.queue.json   { files: ["<name>.asi", "<name>.ini"] }  the Bin files a queue put there
//   Bin/Default.ini.queue-bak  the Default.ini found before a defaultIni job wrote its keys
// A record goes away only after its files are gone / the original is back and read back. Both live in Bin, not in the checkout:
// a worktree shares only game-copy with the main checkout, so a marker under one checkout's probe-out was invisible to queues,
// exit handlers and restores of every other checkout (rule review 11-26). The old <checkout>/probe-out/queue/extra-asi.json
// (LEGACY_MARKER) is still swept. Launch gate: strayAsi(bin) = Bin .asi files no Bin state and no Bin marker accounts for;
// queue.ts and apex-run.ts refuse to start the game while it is not empty.
// Every function takes the Bin dir (and the marker path), so --self-check runs them in a temp dir without the game.
//
// Usage: node tools/harness/extra-asi.ts --self-check
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { COMMUNITY } from './vanilla.ts';

export const extraMarker = (bin: string) => join(bin, 'extra-asi.queue.json');
export const LEGACY_MARKER = resolve(import.meta.dirname, '..', '..', 'probe-out', 'queue', 'extra-asi.json'); // migration only: read, never written
// modules a job may never bring or a marker delete: ours and the ones a Bin state manages (queue.ts parseQueue uses the same list)
export const BLOCKED = /^(ts3native|ApexRadiance|Turbo)\./i;
// every .asi a Bin state may hold (tools/turbo-compat.ts STATES: ApexRadiance, Turbo; ours; the community stack)
export const STATE_ASI = ['ts3native.asi', 'ApexRadiance.asi', 'Turbo.asi', ...COMMUNITY.filter(f => /\.asi$/i.test(f))];
export const INI_BAK = '.queue-bak';
export const extraIni = (asi: string) => asi.replace(/\.asi$/i, '.ini'), extraLog = (asi: string) => asi.replace(/\.asi$/i, '.log');
const sha256 = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');

// the Bin file names a marker owns; anything that is not a plain <name>.asi / <name>.ini is dropped (the list is used for deletes)
export function extraOwned(marker: string): string[] {
  try { const f = JSON.parse(readFileSync(marker, 'utf8')).files; return Array.isArray(f) ? f.filter((x: unknown): x is string => typeof x === 'string' && /^[\w-]+\.(asi|ini)$/i.test(x) && !BLOCKED.test(x)) : []; }
  catch { return []; }
}
// a queue's leftovers are pending (the Bin marker or an old checkout marker)
export const extraPending = (bin: string, legacy = LEGACY_MARKER) => existsSync(extraMarker(bin)) || (!!legacy && existsSync(legacy));
// extraAsiRemove over the Bin marker and the legacy one: '' = all gone, else the reasons
export const extraSweep = (bin: string, logs = false, legacy = LEGACY_MARKER) =>
  [extraMarker(bin), legacy].filter(Boolean).map(m => extraAsiRemove(bin, m, logs)).filter(Boolean).join('; ');
// Bin .asi files that are neither a Bin-state module nor named by the Bin marker (the running job's own module): a launch with
// any of them would load a module nobody asked for (a killed queue of another checkout, a hand copy)
export function strayAsi(bin: string): string[] {
  const ok = new Set([...STATE_ASI, ...extraOwned(extraMarker(bin))].map(f => f.toLowerCase()));
  try { return readdirSync(bin).filter(f => /\.asi$/i.test(f) && !ok.has(f.toLowerCase())); } catch { return []; }
}

// Copies src to bin/<name>.asi with a fresh bin/<name>.ini (iniText) and no old log. Refused, before anything is written,
// when src is missing, its sha256 is not the pin, or bin/<name>.asi or bin/<name>.ini is there and no marker owns it.
export function extraAsiInstall(bin: string, marker: string, src: string, iniText: string, pin?: string): string {
  const name = basename(src), files = [name, extraIni(name)], owned = extraOwned(marker);
  if (!existsSync(src)) throw new Error(`extraAsi: ${src} missing`);
  const have = sha256(src);
  if (pin && have !== pin.toLowerCase()) throw new Error(`extraAsi: ${name} sha256 ${have} is not the pinned ${pin}`);
  for (const f of files) if (existsSync(join(bin, f)) && !owned.includes(f)) throw new Error(`extraAsi: Bin/${f} is already there (not put there by a queue): remove it or pick another name`);
  mkdirSync(dirname(marker), { recursive: true });
  writeFileSync(marker, JSON.stringify({ files: [...new Set([...owned, ...files])] })); // before the copy: a kill from here on is recoverable
  copyFileSync(src, join(bin, name)); writeFileSync(join(bin, files[1]), iniText); rmSync(join(bin, extraLog(name)), { force: true });
  if (sha256(join(bin, name)) !== have) throw new Error(`extraAsi: Bin/${name} read-back differs`);
  return have;
}

// Removes what the marker names (with logs: each module's log too). '' = nothing pending or all gone (marker removed);
// otherwise the reason, and the marker stays for the next try.
export function extraAsiRemove(bin: string, marker: string, logs = false): string {
  if (!existsSync(marker)) return '';
  const owned = extraOwned(marker);
  try { for (const f of owned) { rmSync(join(bin, f), { force: true }); if (logs && /\.asi$/i.test(f)) rmSync(join(bin, extraLog(f)), { force: true }); } }
  catch (e) { return String((e as Error).message ?? e); }
  const left = owned.filter(f => existsSync(join(bin, f)));
  if (left.length) return `still there: ${left.join(', ')}`;
  rmSync(marker, { force: true });
  return '';
}

// Writes text to file after saving the original next to it; returns the original. Refused while an earlier backup is pending.
export function iniBackupWrite(file: string, text: string): string {
  const bak = file + INI_BAK;
  if (existsSync(bak)) throw new Error(`${basename(bak)} exists: the ${basename(file)} of an earlier job is not put back yet`);
  const orig = readFileSync(file);
  writeFileSync(bak, orig); writeFileSync(file, text);
  return orig.toString('utf8');
}
// Puts the saved original back, reads it back, then drops the backup. '' = nothing pending or done; otherwise the reason (backup kept).
export function iniBackupRestore(file: string): string {
  const bak = file + INI_BAK;
  if (!existsSync(bak)) return '';
  try {
    const orig = readFileSync(bak); writeFileSync(file, orig);
    if (!readFileSync(file).equals(orig)) return `${basename(file)} read-back differs from ${basename(bak)}`;
    rmSync(bak); return '';
  } catch (e) { return String((e as Error).message ?? e); }
}
// the "key=value" line of Section.Key as the file holds it ('' when absent): read-back of a merged ini
export function iniLine(text: string, sectionKey: string): string {
  const dot = sectionKey.lastIndexOf('.'), sec = sectionKey.slice(0, dot).toLowerCase(), key = sectionKey.slice(dot + 1).toLowerCase();
  let cur = '';
  for (const l of text.split(/\r?\n/)) {
    const t = l.trim();
    if (t.startsWith('[')) cur = t.slice(1, t.indexOf(']')).toLowerCase();
    else if (cur === sec && t.slice(0, t.indexOf('=')).trim().toLowerCase() === key && t.includes('=')) return t;
  }
  return '';
}

export function selfCheck(): void {
  const d = mkdtempSync(join(tmpdir(), 'extra-asi-')), bin = join(d, 'Bin'), marker = join(d, 'q', 'extra-asi.json'), src = join(d, 'src', 'atlas.asi');
  try {
    mkdirSync(bin); mkdirSync(dirname(src)); writeFileSync(src, 'MZ1');
    const pin = sha256(src);
    assert.throws(() => extraAsiInstall(bin, marker, join(d, 'none.asi'), ''), /missing/);
    assert.throws(() => extraAsiInstall(bin, marker, src, '', 'ab'.repeat(32)), /not the pinned/);
    assert.ok(!existsSync(marker) && !existsSync(join(bin, 'atlas.asi')), 'a refused install writes nothing');
    writeFileSync(join(bin, 'atlas.ini'), 'theirs'); // an ini that is not ours: refused, left alone
    assert.throws(() => extraAsiInstall(bin, marker, src, ''), /atlas\.ini is already there/);
    assert.equal(readFileSync(join(bin, 'atlas.ini'), 'utf8'), 'theirs'); rmSync(join(bin, 'atlas.ini'));
    writeFileSync(join(bin, 'atlas.asi'), 'theirs');
    assert.throws(() => extraAsiInstall(bin, marker, src, ''), /atlas\.asi is already there/); rmSync(join(bin, 'atlas.asi'));
    writeFileSync(join(bin, 'atlas.log'), 'old');
    assert.equal(extraAsiInstall(bin, marker, src, '[WorldSize]\nPhysicsRootM=0\n', pin.toUpperCase()), pin);
    assert.deepEqual(extraOwned(marker), ['atlas.asi', 'atlas.ini']);
    assert.ok(existsSync(join(bin, 'atlas.asi')) && readFileSync(join(bin, 'atlas.ini'), 'utf8').includes('PhysicsRootM=0') && !existsSync(join(bin, 'atlas.log')));
    assert.equal(extraAsiInstall(bin, marker, src, 'x'), pin, 'files the marker owns are ours to replace (killed queue, next job)');
    writeFileSync(join(bin, 'atlas.log'), 'new'); writeFileSync(join(bin, 'other.asi'), 'theirs');
    assert.equal(extraAsiRemove(bin, marker), ''); // the sweep: asi + ini gone, the log and other modules stay
    assert.ok(!existsSync(join(bin, 'atlas.asi')) && !existsSync(join(bin, 'atlas.ini')) && existsSync(join(bin, 'atlas.log')) && existsSync(join(bin, 'other.asi')) && !existsSync(marker));
    extraAsiInstall(bin, marker, src, ''); writeFileSync(join(bin, 'atlas.log'), 'new');
    assert.equal(extraAsiRemove(bin, marker, true), ''); assert.ok(!existsSync(join(bin, 'atlas.log')));
    assert.equal(extraAsiRemove(bin, marker), '', 'nothing pending');
    mkdirSync(dirname(marker), { recursive: true }); writeFileSync(marker, JSON.stringify({ files: ['../x.asi', 'TS3W.exe', 'ts3native.asi', 'a.ini', 7] }));
    assert.deepEqual(extraOwned(marker), ['a.ini'], 'a marker can only name plain asi / ini files, never ours');
    mkdirSync(join(bin, 'a.ini')); writeFileSync(join(bin, 'a.ini', 'x'), ''); // cannot be removed as a file: the marker stays
    assert.notEqual(extraAsiRemove(bin, marker), ''); assert.ok(existsSync(marker));
    rmSync(join(bin, 'a.ini'), { recursive: true }); assert.equal(extraAsiRemove(bin, marker), ''); assert.ok(!existsSync(marker));
    writeFileSync(marker, 'not json'); assert.deepEqual(extraOwned(marker), []);
    writeFileSync(marker, JSON.stringify({ files: ['ApexRadiance.asi', 'Turbo.ini', 'TS3NATIVE.ini', 'atlas.asi'] }));
    assert.deepEqual(extraOwned(marker), ['atlas.asi'], 'the queue.ts block list: ts3native, ApexRadiance, Turbo never owned');
    assert.ok(['ts3native.asi', 'ApexRadiance.asi', 'Turbo.asi'].every(f => BLOCKED.test(f)) && !BLOCKED.test('atlas.asi'));
    rmSync(marker);
    // the marker lives in Bin (seen from every checkout), the launch gate, the legacy marker sweep
    const bm = extraMarker(bin), legacy = join(d, 'old', 'extra-asi.json');
    assert.equal(dirname(bm), bin);
    rmSync(join(bin, 'other.asi'), { force: true }); // the sweep case above leaves it
    for (const f of ['ApexRadiance.asi', 'MonoPatcher.asi', 'ts3native.asi', 'Turbo.asi']) writeFileSync(join(bin, f), 'state');
    assert.deepEqual(strayAsi(bin), [], 'Bin-state modules are never stray');
    writeFileSync(join(bin, 'atlas.asi'), 'left by a killed queue'); writeFileSync(join(bin, 'atlas.ini'), 'x');
    assert.deepEqual(strayAsi(bin), ['atlas.asi'], 'an unowned extra module: the launch refuses');
    rmSync(join(bin, 'atlas.asi')); rmSync(join(bin, 'atlas.ini'));
    assert.ok(!extraPending(bin, legacy));
    extraAsiInstall(bin, bm, src, '');
    assert.ok(existsSync(bm) && extraPending(bin, legacy)); assert.deepEqual(strayAsi(bin), [], 'the job\'s own module (Bin marker) is not stray');
    assert.equal(extraSweep(bin, true, legacy), ''); assert.ok(!existsSync(bm) && !existsSync(join(bin, 'atlas.asi')) && !extraPending(bin, legacy));
    mkdirSync(dirname(legacy)); writeFileSync(legacy, JSON.stringify({ files: ['atlas.asi', 'atlas.ini'] })); writeFileSync(join(bin, 'atlas.asi'), 'x'); writeFileSync(join(bin, 'atlas.ini'), 'x');
    assert.deepEqual(strayAsi(bin), ['atlas.asi'], 'a legacy marker does not excuse a module at launch');
    assert.ok(extraPending(bin, legacy)); assert.equal(extraSweep(bin, false, legacy), '');
    assert.ok(!existsSync(legacy) && !existsSync(join(bin, 'atlas.asi')) && !existsSync(join(bin, 'atlas.ini')), 'migration: an old probe-out marker is honoured');
    for (const f of ['ApexRadiance.asi', 'MonoPatcher.asi', 'ts3native.asi', 'Turbo.asi']) rmSync(join(bin, f));
    // Default.ini backup
    const ini = join(bin, 'Default.ini'); writeFileSync(ini, '[Config]\r\nA=1\r\n');
    assert.equal(iniBackupRestore(ini), '');
    assert.equal(iniBackupWrite(ini, '[Config]\r\nA=1\r\nSceneQuadTree=4096,512,7\r\n'), '[Config]\r\nA=1\r\n');
    assert.ok(existsSync(ini + INI_BAK));
    assert.throws(() => iniBackupWrite(ini, 'x'), /not put back yet/);
    assert.equal(iniLine(readFileSync(ini, 'utf8'), 'Config.SceneQuadTree'), 'SceneQuadTree=4096,512,7');
    assert.equal(iniLine('[Other]\nSceneQuadTree=1\n[config]\n scenequadtree = 8192,512,8 \n', 'Config.SceneQuadTree'), 'scenequadtree = 8192,512,8');
    assert.equal(iniLine('[Other]\nSceneQuadTree=1\n', 'Config.SceneQuadTree'), '');
    assert.equal(iniBackupRestore(ini), ''); assert.equal(readFileSync(ini, 'utf8'), '[Config]\r\nA=1\r\n'); assert.ok(!existsSync(ini + INI_BAK));
  } finally { rmSync(d, { recursive: true, force: true }); }
}

if (process.argv[1] && resolve(process.argv[1]) === resolve(import.meta.filename)) {
  if (!process.argv.includes('--self-check')) { console.error('usage: node tools/harness/extra-asi.ts --self-check'); process.exit(2); }
  selfCheck(); console.log('extra-asi self-check ok');
}
