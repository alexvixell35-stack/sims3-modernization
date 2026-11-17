// DeviceConfig.log compare (docs/284, [Startup] DxDiagCache correctness): the game writes <Documents>\DeviceConfig.log at start
// from its device probe (rating, texture memory, presets). A queue script job records it on the off arm and checks the others.
//   node tools/harness/devcfg-check.ts --record <tag>   copy the stable lines to probe-out/devcfg/<tag>.txt
//   node tools/harness/devcfg-check.ts --expect <tag>   exit 1 when the stable lines differ from <tag>.txt (diff printed)
//   node tools/harness/devcfg-check.ts --self-check
// Stable = every line except the ones that change between two starts of the same machine (free memory).
// TS3_USER: the Sims 3 Documents folder (default <home>\Documents\Electronic Arts\The Sims 3).
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..');
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const DIR = join(ROOT, 'probe-out', 'devcfg');
const VOLATILE = /^(Free memory|Adjusted RAM):/;

export const stable = (text: string) => text.split(/\r?\n/).map(l => l.trimEnd()).filter(l => l && !VOLATILE.test(l));
export function diff(want: string[], got: string[]): string[] {
  const out: string[] = [];
  for (let i = 0; i < Math.max(want.length, got.length); i++) if (want[i] !== got[i]) out.push(`line ${i + 1}: want "${want[i] ?? '<none>'}" got "${got[i] ?? '<none>'}"`);
  return out;
}
const tex = (l: string[]) => l.find(x => x.startsWith('Texture memory:')) ?? 'Texture memory: <none>';

if (process.argv[2] === '--self-check') {
  const a = 'GPU: 5 GPU Memory: 4\r\nFree memory:     9591MB\r\nAdjusted RAM: 31982\r\nTexture memory:  1024MB <<OVERRIDE>>\r\n';
  const b = 'GPU: 5 GPU Memory: 4\r\nFree memory:     8000MB\r\nAdjusted RAM: 31000\r\nTexture memory:  1024MB <<OVERRIDE>>\r\n';
  assert.deepEqual(stable(a), ['GPU: 5 GPU Memory: 4', 'Texture memory:  1024MB <<OVERRIDE>>']);
  assert.deepEqual(diff(stable(a), stable(b)), [], 'free memory and adjusted RAM ignored');
  assert.deepEqual(diff(stable(a), stable(a.replace('1024MB', '32MB'))), ['line 2: want "Texture memory:  1024MB <<OVERRIDE>>" got "Texture memory:  32MB <<OVERRIDE>>"']);
  assert.ok(VOLATILE.test('Free memory:     9591MB') && !VOLATILE.test('Memory:          32494MB') && !VOLATILE.test('Texture memory:  1024MB'));
  console.log('devcfg-check self-check ok');
} else {
  const [mode, tag] = process.argv.slice(2);
  if (!['--record', '--expect'].includes(mode) || !/^[\w.-]+$/.test(tag ?? '')) { console.error('usage: node tools/harness/devcfg-check.ts --record|--expect <tag> | --self-check'); process.exit(2); }
  const src = join(USER, 'DeviceConfig.log');
  if (!existsSync(src)) { console.log(`devcfg: FAIL ${src} missing`); process.exit(1); }
  const got = stable(readFileSync(src, 'latin1')), ref = join(DIR, `${tag}.txt`);
  if (mode === '--record') {
    mkdirSync(DIR, { recursive: true });
    writeFileSync(ref, got.join('\n') + '\n');
    console.log(`devcfg: recorded ${tag} (${got.length} lines; ${tex(got)})`);
  } else {
    if (!existsSync(ref)) { console.log(`devcfg: FAIL no reference ${tag} (run the --record job first)`); process.exit(1); }
    const d = diff(stable(readFileSync(ref, 'latin1')), got);
    console.log(d.length ? `devcfg: FAIL ${d.length} line(s) differ from ${tag}:\n  ${d.join('\n  ')}` : `devcfg: same as ${tag} (${got.length} lines; ${tex(got)})`);
    process.exit(d.length ? 1 : 0);
  }
}
