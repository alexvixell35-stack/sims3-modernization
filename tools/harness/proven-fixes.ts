// The "all proven gameplay fixes" switch set (tools/harness/sets/proven-fixes.txt): checks it against the ts3fixes source and
// prints it for a soak.
//   node tools/harness/proven-fixes.ts            space-separated names (EARLYFIX="$(node tools/harness/proven-fixes.ts)")
//   node tools/harness/proven-fixes.ts --json     {"name":true,...} for `ag patches`
//   node tools/harness/proven-fixes.ts --self-check
// Check: every name in the set and in its excluded comment block is a default-off switch (a `case "name": return
// Instantiator.kX;` in Fixes.cs whose `[Tunable] public static bool kX = false;`), none is in both, and every default-off switch
// is in one of them, so a new fix switch must be sorted into the set or the excluded block before the self-check passes.
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..');
export const SET = join(ROOT, 'tools', 'harness', 'sets', 'proven-fixes.txt');

export function parseSet(text: string): { set: string[]; excluded: string[] } {
  const set: string[] = [], excluded: string[] = [];
  let ex = false;
  for (const line of text.split(/\r?\n/)) {
    if (/^#\s*-{4} excluded/.test(line)) { ex = true; continue; }
    if (!line.trim()) continue;
    if (line.startsWith('#')) { if (ex && /^#\s{3}\S/.test(line)) excluded.push(...line.slice(1).trim().split(/\s+/)); continue; }
    set.push(line.trim());
  }
  return { set, excluded };
}

export function defaultOff(fixes: string, inst: string): Set<string> {
  const def = new Map<string, boolean>();
  for (const m of inst.matchAll(/\[Tunable\]\s*public static bool (k\w+)\s*=\s*(true|false);/g)) def.set(m[1], m[2] === 'true');
  const off = new Set<string>();
  for (const m of fixes.matchAll(/case "([a-z0-9]+)":\s*return Instantiator\.(k\w+);/g)) if (def.get(m[2]) === false) off.add(m[1]);
  return off;
}

export function problems(s: { set: string[]; excluded: string[] }, off: Set<string>): string[] {
  const out: string[] = [], seen = new Set<string>();
  for (const n of [...s.set, ...s.excluded]) {
    if (seen.has(n)) out.push(`${n}: listed twice`);
    seen.add(n);
    if (!off.has(n)) out.push(`${n}: not a default-off ts3fixes switch`);
  }
  for (const n of off) if (!seen.has(n)) out.push(`${n}: default-off switch in neither the set nor the excluded block`);
  return out;
}

function selfCheck() {
  const ok = (c: boolean, what: string) => { if (!c) { console.error('proven-fixes self-check FAILED: ' + what); process.exit(1); } };
  const fx = 'case "a": return Instantiator.kA;\ncase "b": return Instantiator.kB;\ncase "c": return Instantiator.kC;';
  const inst = '[Tunable] public static bool kA = false;\n[Tunable] public static bool kB = false;  // x\n[Tunable] public static bool kC = true;';
  const off = defaultOff(fx, inst);
  ok(off.size === 2 && off.has('a') && off.has('b'), 'default-off parse');
  const s = parseSet('# head\na\n\n# ---- excluded (x)\n# why:\n#   b\n');
  ok(s.set.join() === 'a' && s.excluded.join() === 'b', 'set parse');
  ok(problems(s, off).length === 0, 'clean fixture');
  ok(problems(parseSet('a\nc\n'), off).length === 2, 'non-switch and missing switch flagged');
  const real = problems(parseSet(readFileSync(SET, 'utf8')), defaultOff(readFileSync(join(ROOT, 'mods/ts3fixes/src/Fixes.cs'), 'utf8'), readFileSync(join(ROOT, 'mods/ts3fixes/src/Instantiator.cs'), 'utf8')));
  ok(real.length === 0, 'repo set: ' + real.join('; '));
  console.log('proven-fixes self-check: ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const { set } = parseSet(readFileSync(SET, 'utf8'));
  console.log(process.argv.includes('--json') ? JSON.stringify(Object.fromEntries(set.map(n => [n, true]))) : set.join(' '));
}
