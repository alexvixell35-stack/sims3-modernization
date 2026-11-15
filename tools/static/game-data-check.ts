// Read-only checks of shipped game data, for leads whose reach depends on tuning or CAS parts (docs/582 B8 notes).
// Reads the installed .package files (never writes them); local-only, needs TS3_BIN (~/dev/ts3-env.sh).
//   node tools/static/game-data-check.ts tuning <regex>          XML tuning resources (GameplayData.package and every
//                                                                 Packages/DeltaPackages package) whose text matches
//   node tools/static/game-data-check.ts casp <Name1,Name2,...>   for each age/sex, which names have a CAS part under
//                                                                 FacePaintingBooth-style prefixes (ageSex, ageU, uSex, uu)
//   node tools/static/game-data-check.ts --self-check            the prefix rule (no game files needed)
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { readIndex, refpack } from '../extract-assemblies.ts';
import { fnv64 } from '../dbpf.ts';

const XML = 0x0333406c, CASP = 0x034aeecb;

function* packages(dir: string): Generator<string> {
  let names: string[];
  try { names = readdirSync(dir); } catch { return; }
  for (const n of names) {
    const p = join(dir, n);
    let s;
    try { s = statSync(p); } catch { continue; }
    if (s.isDirectory()) { if (!/Library|Thumbnails|Collections/.test(n)) yield* packages(p); }
    else if (n.endsWith('.package')) yield p;
  }
}

function gameFiles(): string[] {
  const bin = process.env.TS3_BIN;
  if (!bin) throw new Error('TS3_BIN not set: source ~/dev/ts3-env.sh');
  const root = join(bin, '..', '..');
  const out = [join(bin, 'Gameplay', 'GameplayData.package'), join(bin, 'gameplay.package')];
  for (const d of readdirSync(root)) if (d === 'GameData' || /^(EP|SP)\d+$/.test(d)) out.push(...packages(join(root, d, 'GameData')));
  return [...new Set(out)];
}

function body(b: Buffer, e: { offset: number; fileSize: number; compressed: number }): Buffer {
  const d = b.subarray(e.offset, e.offset + e.fileSize);
  return e.compressed === 0xffff ? refpack(d) : d;
}

export function casPrefixes(age: string, sex: string): string[] {
  return [age + sex, age + 'u', 'u' + sex, 'uu'];
}

const [mode, arg] = process.argv.slice(2);
if (mode === '--self-check') {
  // EA's GetPrefixStrings for an elder woman: "ef", "eu", "uf", "uu"
  const got = casPrefixes('e', 'f').join(',');
  if (got !== 'ef,eu,uf,uu') { console.log('game-data-check self-check: FAIL prefixes ' + got); process.exit(1); }
  console.log('game-data-check self-check: ok');
} else if (mode === 'tuning' && arg) {
  const re = new RegExp(arg);
  for (const f of gameFiles()) {
    let b: Buffer;
    try { b = readFileSync(f); } catch { continue; }
    for (const e of readIndex(b)) {
      if (e.type !== XML) continue;
      let t: string;
      try { t = body(b, e).toString('latin1'); } catch { continue; }
      if (!re.test(t)) continue;
      console.log(`== ${f.split('/').slice(-3).join('/')} ${e.inst.toString(16)}`);
      for (const line of t.split(/\r?\n/)) if (re.test(line) || /value=/.test(line) && process.env.ALL) console.log(line);
    }
  }
} else if (mode === 'casp' && arg) {
  const have = new Set<bigint>();
  for (const f of gameFiles()) {
    let b: Buffer;
    try { b = readFileSync(f); } catch { continue; }
    for (const e of readIndex(b)) if (e.type === CASP) have.add(e.inst);
  }
  const names = arg.split(',');
  for (const age of ['c', 't', 'a', 'e']) for (const sex of ['f', 'm']) {
    const missing = names.filter(n => !casPrefixes(age, sex).some(p => have.has(fnv64(p + n))));
    console.log(`${age}${sex}: ${names.length - missing.length}/${names.length}${missing.length ? ' missing ' + missing.join(' ') : ''}`);
  }
} else {
  console.log('usage: game-data-check.ts tuning <regex> | casp <Name1,Name2,...>');
  process.exit(mode ? 1 : 0);
}
