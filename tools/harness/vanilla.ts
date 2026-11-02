// Vanilla mode (docs/236 "Vanilla harness baseline"): the game runs without our mod, only the ts3agent harness package.
//   TS3_VANILLA=1 is opt-in; without it nothing here is used and the Steam machine behaves as before.
//   node tools/harness/vanilla.ts install            move the ts3native pair (+ its outputs), ts3fixes and the script cache out; install TS3_AGENT_PACKAGE
//   node tools/harness/vanilla.ts status [--tag T]   read-back of the state; writes probe-out/<T>.vanilla-status.txt (sha + listings)
//   node tools/harness/vanilla.ts restore            move everything back, compare sha256 with the manifest
//   node tools/harness/vanilla.ts saves-backup|saves-restore   robocopy /MIR of Saves to "<stash> saves\Saves" (incremental after the first run)
//   node tools/harness/vanilla.ts rm-save <name>     remove a scratch save (a .sims3 DIRECTORY: rm -f does not remove it)
//   node tools/harness/vanilla.ts --self-check       no game
// Windows paths by nature (Program Files Bin, Documents), same as cycle.ts.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { BIN } from './game-exe.ts';

export const vanilla = () => process.env.TS3_VANILLA === '1';
const ROOT = resolve(import.meta.dirname, '..', '..');
export interface Spec { bin: string; user: string; stash: string; agent?: string }
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
export const defaultSpec = (): Spec => ({ bin: BIN, user: USER, stash: process.env.TS3_VANILLA_STASH ?? `${USER} vanilla-stash`, agent: process.env.TS3_AGENT_PACKAGE });
const sha = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');
const mfile = (s: Spec) => join(s.stash, 'manifest.json');
interface Entry { rel: string; from: string; to: string; size: number; sha: string }
interface Manifest { entries: Entry[]; agentOld?: Entry; agentNew?: string; wininet?: string; d3d9?: string; time: string }

function move(from: string, to: string) { mkdirSync(dirname(to), { recursive: true }); renameSync(from, to); } // same drive: a rename, no copy

// what leaves: Bin pair + outputs, Mods ts3fixes, the script cache (rebuilt by the game from the vanilla Mods set)
const items = (s: Spec): { rel: string; from: string }[] => [
  ...['ts3native.asi', 'ts3native.ini', 'ts3native.log', 'autshadow.jsonl'].map(f => ({ rel: `Bin/${f}`, from: join(s.bin, f) })),
  ...readdirSync(join(s.user, 'Mods', 'Packages')).filter(f => /^ts3fixes.*\.package$/i.test(f)).map(f => ({ rel: `Mods/Packages/${f}`, from: join(s.user, 'Mods', 'Packages', f) })),
  { rel: 'scriptCache.package', from: join(s.user, 'scriptCache.package') },
].filter(i => existsSync(i.from));

export function problems(s: Spec): string[] {
  const out: string[] = [];
  if (!existsSync(join(s.bin, 'Sims3Launcher.exe')) && !existsSync(join(s.bin, 'TS3W.exe'))) return [`Bin ${s.bin} is not a game Bin (set TS3_BIN)`];
  const asis = (d: string): string[] => existsSync(d) ? readdirSync(d, { recursive: true }).map(String).filter(n => /\.asi$/i.test(n)) : [];
  for (const a of asis(s.bin)) out.push(`Bin has ${a}`);
  for (const f of ['ts3native.ini', 'ts3native.log', 'autshadow.jsonl']) if (existsSync(join(s.bin, f))) out.push(`Bin has ${f}`);
  for (const d of ['scripts', 'plugins']) if (existsSync(join(s.bin, d))) out.push(`Bin has ${d}\\`);
  for (const f of readdirSync(join(s.user, 'Mods', 'Packages'))) if (/^ts3fixes/i.test(f)) out.push(`Mods has ${f}`);
  if (!existsSync(mfile(s))) { out.push('no stash manifest (install not run)'); return out; }
  const m: Manifest = JSON.parse(readFileSync(mfile(s), 'utf8'));
  const ag = join(s.user, 'Mods', 'Packages', 'ts3agent.package');
  if (!existsSync(ag)) out.push('ts3agent.package missing');
  else if (m.agentNew && sha(ag) !== m.agentNew) out.push('ts3agent.package sha differs from the manifest');
  for (const [k, v] of [['wininet.dll', m.wininet], ['d3d9.dll', m.d3d9]] as const) if (v && existsSync(join(s.bin, k)) && sha(join(s.bin, k)) !== v) out.push(`${k} sha differs from the manifest`);
  return out;
}

export function install(s: Spec) {
  const pre = problems({ ...s, stash: join(s.stash, 'none') }).filter(p => /not a game Bin/.test(p));
  if (pre.length) throw new Error(pre[0]);
  if (existsSync(mfile(s))) throw new Error(`stash manifest exists (${mfile(s)}): restore first`);
  if (existsSync(join(ROOT, 'probe-out', 'queue', 'job-running'))) throw new Error('probe-out/queue/job-running exists');
  const list = items(s), entries: Entry[] = [];
  const manifest: Manifest = { entries, time: new Date().toISOString() };
  for (const i of list) entries.push({ rel: i.rel, from: i.from, to: join(s.stash, i.rel), size: statSync(i.from).size, sha: sha(i.from) });
  const rec = (f: string) => existsSync(join(s.bin, f)) ? sha(join(s.bin, f)) : undefined;
  manifest.wininet = rec('wininet.dll'); manifest.d3d9 = rec('d3d9.dll');
  const ag = join(s.user, 'Mods', 'Packages', 'ts3agent.package');
  if (s.agent) {
    if (!existsSync(s.agent)) throw new Error(`TS3_AGENT_PACKAGE missing: ${s.agent}`);
    if (existsSync(ag)) manifest.agentOld = { rel: 'Mods/Packages/ts3agent.package.old', from: ag, to: join(s.stash, 'Mods', 'Packages', 'ts3agent.package.old'), size: statSync(ag).size, sha: sha(ag) };
    manifest.agentNew = sha(s.agent);
  }
  mkdirSync(s.stash, { recursive: true });
  writeFileSync(mfile(s), JSON.stringify(manifest, null, 1)); // manifest first
  for (const e of entries) move(e.from, e.to);
  if (manifest.agentOld) move(manifest.agentOld.from, manifest.agentOld.to);
  if (s.agent) copyFileSync(s.agent, ag);
  for (const e of entries) { // read back: new place equal, old place empty
    if (!existsSync(e.to) || sha(e.to) !== e.sha) throw new Error(`read-back: ${e.to} differs from the manifest`);
    if (existsSync(e.from)) throw new Error(`read-back: ${e.from} still exists`);
  }
  if (manifest.agentOld && sha(manifest.agentOld.to) !== manifest.agentOld.sha) throw new Error('read-back: old ts3agent.package differs');
  const p = problems(s);
  if (p.length) throw new Error(`vanilla state check failed: ${p.join('; ')}`);
  return manifest;
}

export function restore(s: Spec) {
  if (!existsSync(mfile(s))) throw new Error('no manifest: nothing to restore');
  const m: Manifest = JSON.parse(readFileSync(mfile(s), 'utf8')), ag = join(s.user, 'Mods', 'Packages', 'ts3agent.package');
  const all = [...m.entries, ...(m.agentOld ? [m.agentOld] : [])];
  for (const e of all) if (!existsSync(e.to) && !existsSync(e.from)) throw new Error(`lost: ${e.rel} is in neither place; stash kept`);
  if (m.agentOld && existsSync(m.agentOld.to)) rmSync(ag, { force: true }); // the vanilla-run agent build goes, the old one returns
  rmSync(join(s.user, 'scriptCache.package'), { force: true }); // the game's cache of the vanilla set; the stashed one returns
  for (const e of all) if (existsSync(e.to)) move(e.to, e.from);
  const bad = all.filter(e => !existsSync(e.from) || sha(e.from) !== e.sha).map(e => e.rel);
  if (bad.length) throw new Error(`restore read-back failed: ${bad.join(', ')}; stash kept`);
  rmSync(s.stash, { recursive: true, force: true });
  return all.length;
}

// A saved game is a DIRECTORY (<name>.sims3\...): rm -f on it fails silently (run 7k left ea7k-scratch.sims3 behind).
export function rmSave(saves: string, name: string) {
  const n = name.replace(/\.sims3$/i, ''), d = join(saves, `${n}.sims3`);
  if (/^baseline-/i.test(n) || /[\\/]/.test(n)) throw new Error(`refusing to remove ${n}`);
  rmSync(d, { recursive: true, force: true });
  rmSync(`${d}.backup`, { recursive: true, force: true });
  if (existsSync(d)) throw new Error(`read-back: ${d} still exists`);
}

const robo = (a: string, b: string) => { // robocopy exit codes 0-7 are fine
  try { execFileSync('robocopy', [a, b, '/MIR', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NP', '/MT:8'], { stdio: 'ignore' }); } catch (e: any) { if (e.status > 7) throw new Error(`robocopy ${a} -> ${b} exit ${e.status}`); }
};
const savesCopy = (s: Spec) => join(`${s.stash} saves`, 'Saves'); // outside the stash: restore() deletes the stash
export const savesBackup = (s: Spec) => robo(join(s.user, 'Saves'), savesCopy(s));
export const savesRestore = (s: Spec) => robo(savesCopy(s), join(s.user, 'Saves'));

function status(s: Spec, tag?: string) {
  const lines: string[] = [`# vanilla status ${new Date().toISOString()}`];
  const ls = (d: string) => readdirSync(d).map(f => { const p = join(d, f), st = statSync(p); return `${f}\t${st.isDirectory() ? 'dir' : st.size}${st.isFile() && /\.(asi|dll|package|ini)$/i.test(f) ? '\t' + sha(p).slice(0, 12) : ''}`; });
  lines.push('## Bin', ...ls(s.bin), '## Mods/Packages', ...ls(join(s.user, 'Mods', 'Packages')));
  const p = problems(s); lines.push(p.length ? `PROBLEMS: ${p.join('; ')}` : 'vanilla state OK');
  if (tag) { mkdirSync(join(ROOT, 'probe-out'), { recursive: true }); writeFileSync(join(ROOT, 'probe-out', `${tag}.vanilla-status.txt`), lines.join('\n') + '\n'); }
  console.log(lines.filter(l => !l.includes('\t') || /ts3|wininet|d3d9|\.asi/i.test(l)).join('\n'));
  return p;
}

function selfCheck() {
  const t = mkdtempSync(join(tmpdir(), 'vanilla-')), mk = (f: string, c: string) => { mkdirSync(dirname(f), { recursive: true }); writeFileSync(f, c); };
  try {
    const s: Spec = { bin: join(t, 'Bin'), user: join(t, 'U'), stash: join(t, 'U vanilla-stash'), agent: join(t, 'new-agent.package') };
    const pk = (f: string) => join(s.user, 'Mods', 'Packages', f);
    mk(join(s.bin, 'TS3W.exe'), 'exe'); mk(join(s.bin, 'ts3native.asi'), 'asi'); mk(join(s.bin, 'ts3native.ini'), 'ini'); mk(join(s.bin, 'ts3native.asi.pre-7k'), 'old'); mk(join(s.bin, 'wininet.dll'), 'ual');
    mk(pk('ts3fixes.package'), 'fx'); mk(pk('ts3agent.package'), 'old-agent'); mk(pk('ts3-nointro.package'), 'ni'); mk(join(s.user, 'scriptCache.package'), 'cache'); mk(s.agent!, 'new-agent');
    const must = (c: boolean, m: string) => { if (!c) throw new Error(m); };
    must(problems(s).length >= 3, 'a normal state must report problems');
    must(problems({ ...s, bin: join(t, 'nope') }).some(p => /not a game Bin/.test(p)), 'wrong Bin must be flagged');
    install(s);
    must(problems(s).length === 0, `vanilla state should be clean: ${problems(s)}`);
    must(existsSync(pk('ts3-nointro.package')) && existsSync(join(s.bin, 'wininet.dll')) && existsSync(join(s.bin, 'ts3native.asi.pre-7k')), 'nointro/wininet/.pre-* must stay');
    must(readFileSync(pk('ts3agent.package'), 'utf8') === 'new-agent', 'agent not installed');
    try { install(s); must(false, 'second install must refuse'); } catch (e: any) { must(/manifest exists/.test(e.message), e.message); }
    mk(join(s.bin, 'scripts', 'x.asi'), 'x'); // UAL would load it
    must(problems(s).some(p => /scripts/.test(p)), 'scripts\\ folder not flagged');
    rmSync(join(s.bin, 'scripts'), { recursive: true });
    renameSync(join(s.stash, 'Bin', 'ts3native.ini'), join(t, 'hide')); // lost file -> restore fails, stash kept
    try { restore(s); must(false, 'lost file must fail'); } catch (e: any) { must(/lost/.test(e.message), e.message); }
    renameSync(join(t, 'hide'), join(s.stash, 'Bin', 'ts3native.ini'));
    restore(s);
    const rd = (f: string) => readFileSync(f, 'utf8');
    must(rd(join(s.bin, 'ts3native.asi')) === 'asi' && rd(pk('ts3agent.package')) === 'old-agent' && rd(join(s.user, 'scriptCache.package')) === 'cache' && existsSync(pk('ts3fixes.package')), 'restore incomplete');
    must(!existsSync(s.stash), 'stash not removed');
    // scratch save = directory
    const saves = join(t, 'Saves'); mk(join(saves, 'ea7k-scratch.sims3', 'Meta.data'), 'm'); mk(join(saves, 'ea7k-scratch.sims3.backup', 'Meta.data'), 'm'); mk(join(saves, 'Baseline-SV-Spring.sims3', 'Meta.data'), 'b');
    rmSave(saves, 'ea7k-scratch.sims3');
    must(!existsSync(join(saves, 'ea7k-scratch.sims3')) && !existsSync(join(saves, 'ea7k-scratch.sims3.backup')), 'scratch save directory not removed');
    try { rmSave(saves, 'Baseline-SV-Spring'); must(false, 'Baseline must be refused'); } catch (e: any) { must(/refusing/.test(e.message), e.message); }
    must(existsSync(join(saves, 'Baseline-SV-Spring.sims3')), 'Baseline removed');
    console.log('vanilla.ts self-check OK');
  } finally { rmSync(t, { recursive: true, force: true }); }
}

if (import.meta.main) {
  const [cmd, arg] = process.argv.slice(2), s = defaultSpec(), tag = process.argv.includes('--tag') ? process.argv[process.argv.indexOf('--tag') + 1] : undefined;
  if (cmd === '--self-check') selfCheck();
  else if (cmd === 'install') { install(s); console.log('vanilla installed'); status(s, tag); }
  else if (cmd === 'status') { if (status(s, tag).length) process.exit(1); }
  else if (cmd === 'restore') console.log(`restored ${restore(s)} files`);
  else if (cmd === 'saves-backup') savesBackup(s);
  else if (cmd === 'saves-restore') savesRestore(s);
  else if (cmd === 'rm-save' && arg) rmSave(join(s.user, 'Saves'), arg);
  else { console.error('usage: vanilla.ts install|status [--tag T]|restore|saves-backup|saves-restore|rm-save <name>|--self-check'); process.exit(2); }
}
