// Harness scratch files a repro wrote and must remove (repro.ts local step "delete_scratch"): a scratch save
// <user>\Saves\<name>.sims3 (+ .backup) whose name starts with "Repro-" (never a Baseline save), and the Boundless Atlas
// placement sidecars <user>\ts3native\atlas\*.place.jsonl(.tmp). Nothing else can be named.
//   node tools/harness/scratch-files.ts --self-test
import { existsSync, readdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';

export function scratchTargets(user: string, args: { saves?: string[]; placement?: boolean }, list: (dir: string) => string[] = d => existsSync(d) ? readdirSync(d) : []): string[] {
  const out: string[] = [];
  for (const n of args.saves ?? []) {
    if (!/^Repro-[A-Za-z0-9-]+$/.test(n)) throw new Error(`delete_scratch: ${n} is no Repro-* scratch save name`);
    for (const f of [`${n}.sims3`, `${n}.sims3.backup`]) out.push(join(user, 'Saves', f));
  }
  if (args.placement) {
    const dir = join(user, 'ts3native', 'atlas');
    for (const f of list(dir)) if (/^[0-9a-f]{16}(-[0-9a-f]{8})?\.place\.jsonl(\.tmp)?$/.test(f)) out.push(join(dir, f));
  }
  return out;
}

// removes what exists; the line lists what went
export function deleteScratch(user: string, args: { saves?: string[]; placement?: boolean }): string {
  const gone: string[] = [];
  for (const p of scratchTargets(user, args)) if (existsSync(p)) { rmSync(p, { recursive: true, force: true }); gone.push(p.slice(user.length + 1)); }
  return `deleted ${gone.length}${gone.length ? ': ' + gone.join(', ') : ''}`;
}

if (process.argv[2] === '--self-test') {
  const assert = await import('node:assert/strict');
  const U = 'U';
  const ls = () => ['0123456789abcdef-89abcdef.place.jsonl', '0123456789abcdef.place.jsonl.tmp', '0123456789abcdef.seed.jsonl', 'x.place.jsonl'];
  assert.deepEqual(scratchTargets(U, { saves: ['Repro-AtlasPark-1126'] }, ls), [join(U, 'Saves', 'Repro-AtlasPark-1126.sims3'), join(U, 'Saves', 'Repro-AtlasPark-1126.sims3.backup')]);
  assert.deepEqual(scratchTargets(U, { placement: true }, ls), [join(U, 'ts3native', 'atlas', '0123456789abcdef-89abcdef.place.jsonl'), join(U, 'ts3native', 'atlas', '0123456789abcdef.place.jsonl.tmp')]);
  assert.throws(() => scratchTargets(U, { saves: ['Baseline-SV'] }, ls), /no Repro-\*/);
  assert.throws(() => scratchTargets(U, { saves: ['Repro-..\\x'] }, ls), /no Repro-\*/);
  console.log('scratch-files selftest: PASS');
}
