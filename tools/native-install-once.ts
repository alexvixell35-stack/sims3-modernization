// Every ts3native install function is called once. An install reads what a call site calls as "the original" and points
// the site at our hook; called twice, the hook becomes its own original and calls itself until the stack ends
// (2026-11-20, docs/679 §8: `InstallWpGather(ini);` stood twice in ts3native.cpp after a merge, the first route job hung).
//   node tools/native-install-once.ts               list install calls that stand more than once in the product's .cpp files
//                                                   (not selftest.cpp / *-test.cpp, which call installs on purpose); exit 1 if any
//   node tools/native-install-once.ts --self-check  the scanner's own cases, then the same scan
import { readFileSync, readdirSync } from 'node:fs';
import { join, resolve } from 'node:path';

// a call statement `InstallX(ini)` / `ApplyX(ini)` (also `keepLog = ApplyX(ini) || keepLog;`); a definition has a type
// before `ini` and a declaration ends in `(const wchar_t* ini);`, so neither matches
const CALL = /\b((?:Install|Apply)[A-Za-z0-9_]+)\(ini\)/g;

export type Dup = { fn: string; file: string; lines: number[] };

export function scan(text: string, file: string): Map<string, Dup> {
  const seen = new Map<string, Dup>();
  text.split(/\r?\n/).forEach((line, i) => {
    const code = line.replace(/\/\/.*$/, ''); // a call named in a comment is not a call
    for (const m of code.matchAll(CALL)) {
      const d = seen.get(m[1]) ?? { fn: m[1], file, lines: [] };
      d.lines.push(i + 1);
      seen.set(m[1], d);
    }
  });
  return seen;
}

// the self-test sources call installs on purpose, several times (selftest.cpp, *-test.cpp): only the product's files count
export const isProduct = (name: string): boolean => name.endsWith('.cpp') && name !== 'selftest.cpp' && !name.endsWith('-test.cpp');

export function duplicates(files: { name: string; text: string }[]): Dup[] {
  const all = new Map<string, Dup[]>();
  for (const f of files) for (const d of scan(f.text, f.name).values()) all.set(d.fn, [...(all.get(d.fn) ?? []), d]);
  const out: Dup[] = [];
  for (const ds of all.values()) if (ds.reduce((n, d) => n + d.lines.length, 0) > 1) out.push(...ds);
  return out;
}

function selfCheck(): void {
  const ok = (c: boolean, what: string) => { if (!c) { console.error(`self-check FAILED: ${what}`); process.exit(1); } };
  // real lines of ts3native.cpp (the two that hung the game, a keepLog form, a definition, a declaration, a comment)
  const twice = [
    '        InstallWpSnap(ini);      // docs/679: after [RouteSnap] (the write sequence); two call sites of its own',
    '        InstallWpGather(ini);    // docs/679 §5: after InstallWpSnap (chains over its two call sites)',
    '        InstallWpGather(ini);    // docs/679 §5: after InstallWpSnap (chains over its two call sites)',
    '        keepLog = ApplyHitchSum(ini) || keepLog;        // docs/666 §5: after [RouteJobPhases] (SyncPlans)',
  ].join('\n');
  const d = duplicates([{ name: 'a.cpp', text: twice }]);
  ok(d.length === 1 && d[0].fn === 'InstallWpGather' && d[0].lines.join() === '2,3', 'the doubled call is found, the single ones are not');
  ok(scan(twice, 'a.cpp').has('ApplyHitchSum') && scan(twice, 'a.cpp').has('InstallWpSnap'), 'both call forms are seen');
  const none = [
    'static void InstallWpGather(const wchar_t* ini) {',
    'static void InstallWpGather(const wchar_t* ini);',
    '        InstallWpGather(ini);    // InstallWpGather(ini) once: after InstallWpSnap(ini)',
    '// InstallWpGather(ini);',
  ].join('\n');
  ok(duplicates([{ name: 'a.cpp', text: none }]).length === 0, 'definition, declaration and comments are not calls');
  ok(duplicates([{ name: 'a.cpp', text: '    InstallX(ini);' }, { name: 'b.cpp', text: '    InstallX(ini);' }]).length === 2, 'two files count together');
  ok(isProduct('ts3native.cpp') && isProduct('wpgather.cpp') && !isProduct('selftest.cpp') && !isProduct('steerro-test.cpp') && !isProduct('wpgather-core.h'), 'product files only');
  console.log('native-install-once self-check ok');
}

function main(): number {
  if (process.argv.includes('--self-check')) selfCheck();
  const src = resolve(import.meta.dirname, '..', 'native', 'ts3native');
  const files = readdirSync(src).filter(isProduct).sort().map((f) => ({ name: f, text: readFileSync(join(src, f), 'utf8') }));
  const calls = files.reduce((n, f) => n + [...scan(f.text, f.name).values()].reduce((k, d) => k + d.lines.length, 0), 0);
  const dups = duplicates(files);
  for (const d of dups) console.error(`install call more than once: ${d.fn}(ini) at native/ts3native/${d.file}:${d.lines.join(', ')}`);
  if (!calls) { console.error('no install call found: the scanner no longer matches ts3native.cpp'); return 1; }
  console.log(`native-install-once: ${calls} install calls in ${files.length} files, ${dups.length ? 'FAIL' : 'each once'}`);
  return dups.length ? 1 : 0;
}

if (process.argv[1] && resolve(process.argv[1]) === import.meta.filename) process.exit(main());
