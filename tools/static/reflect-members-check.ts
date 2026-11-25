// Reflection names in ts3fixes against the EA sources: every `typeof(T).GetField("n"` / `GetProperty("n"` /
// `GetMethod("n"` in mods/ts3fixes/src is looked up in the decompiled EA tree (game-copy/decompiled, gitignored) and
// flagged when the member is the other kind (a field asked as a property, as homeworkskillbonus did with Homework.IsOld,
// live 2026-11-15) or is missing from every class named T. Read only; local-only (needs game-copy).
//   node tools/static/reflect-members-check.ts [--all]      list problems (exit 1 when any)
//   node tools/static/reflect-members-check.ts --self-check
import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..');
type Kind = 'field' | 'property' | 'method';

// Kinds a class body declares for `name` (decompiled C#: one member per line, tab indented).
export function memberKinds(src: string, name: string): Set<Kind> {
  const k = new Set<Kind>();
  const esc = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  for (const line of src.split('\n')) {
    const l = line.trim();
    if (!/^(public|private|protected|internal|static|readonly|const|new|override|virtual|abstract|sealed|\[)/.test(l)) continue;
    if (new RegExp(`\\b${esc}\\s*\\(`).test(l) && !/[=]/.test(l.split('(')[0])) k.add('method');
    else if (new RegExp(`\\b${esc}\\s*(;|=[^>])`).test(l) && !/\(/.test(l.split(name)[0])) k.add('field');
    else if (new RegExp(`\\b${esc}\\s*(=>|\\{|$)`).test(l)) k.add('property');
  }
  return k;
}

export interface Ask { file: string; line: number; type: string; name: string; kind: Kind }
export function asks(src: string, file: string): Ask[] {
  const out: Ask[] = [];
  const re = /typeof\(([A-Za-z0-9_.<>, ]+?)\)\.Get(Field|Property|Method)\("([A-Za-z0-9_]+)"/g;
  src.split('\n').forEach((l, i) => {
    for (const m of l.matchAll(re)) {
      const t = m[1].replace(/<.*>/, '').split('.').pop()!;
      out.push({ file, line: i + 1, type: t, name: m[3], kind: m[2].toLowerCase() as Kind });
    }
  });
  return out;
}

function walk(dir: string, acc: string[] = []): string[] {
  for (const e of readdirSync(dir)) { const p = join(dir, e); if (statSync(p).isDirectory()) walk(p, acc); else if (p.endsWith('.cs')) acc.push(p); }
  return acc;
}

// class name -> the source of every class body declared with that name (whole file: decompiled files hold one type;
// nested types are found in their outer type's file)
function classIndex(files: string[]): Map<string, string[]> {
  const idx = new Map<string, string[]>();
  for (const f of files) {
    const s = readFileSync(f, 'utf8');
    for (const m of s.matchAll(/\b(?:class|struct|interface)\s+([A-Za-z0-9_]+)/g)) {
      const a = idx.get(m[1]) ?? []; a.push(s); idx.set(m[1], a);
    }
  }
  return idx;
}

function selfCheck(): void {
  const src = 'public class A\n{\n\tpublic bool IsOld;\n\tprivate int mX = 3;\n\tpublic float Score => mScore;\n\tpublic int Count\n\t{\n\tpublic void Run(int a)\n}\n';
  const eq = (a: Set<Kind>, b: Kind[]) => a.size === b.length && b.every(x => a.has(x));
  const fails: string[] = [];
  if (!eq(memberKinds(src, 'IsOld'), ['field'])) fails.push('IsOld field');
  if (!eq(memberKinds(src, 'mX'), ['field'])) fails.push('mX field');
  if (!eq(memberKinds(src, 'Score'), ['property'])) fails.push('Score property');
  if (!eq(memberKinds(src, 'Count'), ['property'])) fails.push('Count property');
  if (!eq(memberKinds(src, 'Run'), ['method'])) fails.push('Run method');
  const a = asks('x = typeof(Homework).GetProperty("IsOld", kInstAll); y = typeof(Outer.Inner).GetField("mY");', 'f.cs');
  if (a.length !== 2 || a[0].type !== 'Homework' || a[0].kind !== 'property' || a[1].type !== 'Inner') fails.push('asks');
  if (fails.length) { console.log('reflect-members-check self-check FAILED: ' + fails.join(', ')); process.exit(1); }
  console.log('reflect-members-check self-check ok');
}

const isMain = (process.argv[1] ?? '').endsWith('reflect-members-check.ts');
if (!isMain) { /* imported */ }
else if (process.argv.includes('--self-check')) selfCheck();
else {
  const dec = join(ROOT, 'game-copy', 'decompiled');
  if (!existsSync(dec)) { console.log('reflect-members-check: no game-copy/decompiled (local-only), skipped'); process.exit(0); }
  const idx = classIndex(walk(dec));
  const srcDir = join(ROOT, 'mods', 'ts3fixes', 'src');
  let bad = 0, seen = 0, unknownType = 0;
  for (const f of readdirSync(srcDir).filter(x => x.endsWith('.cs'))) {
    for (const q of asks(readFileSync(join(srcDir, f), 'utf8'), f)) {
      const bodies = idx.get(q.type);
      if (!bodies) { unknownType++; if (process.argv.includes('--all')) console.log(`?  ${q.file}:${q.line} ${q.type}.${q.name} (type not in the EA tree: ours or engine)`); continue; }
      seen++;
      const kinds = new Set<Kind>(); for (const b of bodies) for (const k of memberKinds(b, q.name)) kinds.add(k);
      if (kinds.has(q.kind)) continue;
      // a member may be inherited (declared on a base class): only a member found as the OTHER kind is certain
      if (kinds.size === 0) { if (process.argv.includes('--all')) console.log(`?  ${q.file}:${q.line} ${q.type}.${q.name}: not declared in ${q.type} (inherited?)`); continue; }
      bad++;
      console.log(`BAD ${q.file}:${q.line} Get${q.kind[0].toUpperCase() + q.kind.slice(1)}("${q.name}") on ${q.type}, which declares it as ${[...kinds].join('/')}`);
    }
  }
  console.log(`reflect-members-check: ${seen} lookups checked, ${bad} wrong kind, ${unknownType} on types outside the EA tree`);
  process.exit(bad ? 1 : 0);
}
