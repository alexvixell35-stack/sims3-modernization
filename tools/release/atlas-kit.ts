// [Kit] Release=1 for every Boundless Atlas copy a release, kit or tester tool stages: with it atlas.asi refuses the
// test-only StripProve bypasses (boundless-atlas native/atlas/rebase.cpp, [Kit] section of atlas.ini).
//   node tools/release/atlas-kit.ts --self-check
// queue.ts adds ATLAS_KIT_INI to the extraAsi.ini of release jobs; experimental.ts, build-kit-ab.ts and apex-testkit/kit.ts do
// not stage Atlas yet, so their self-checks run atlasKitProblem on their own source: the day one of them names the Atlas
// asi or ini, it must also write Kit.Release (or use ATLAS_KIT_INI), or its self-check fails.
import assert from 'node:assert/strict';

export const ATLAS_KIT_INI = { 'Kit.Release': 1 } as const;

// '' = fine; else what is missing. Names the Atlas files by pattern so this file's own source does not trip it.
export function atlasKitProblem(src: string): string {
  const stages = new RegExp('\\batlas(-oop)?\\.(asi|ini)\\b', 'i').test(src);
  const writes = /ATLAS_KIT_INI|Kit\.Release|\[Kit\][^\n]*\n?\s*Release\s*=\s*1/.test(src);
  return stages && !writes ? 'stages Atlas without [Kit] Release=1 (import ATLAS_KIT_INI from tools/release/atlas-kit.ts)' : '';
}

export function selfCheck() {
  assert.equal(atlasKitProblem('copy game-copy/x/README.txt'), '');
  assert.match(atlasKitProblem("cp('game-copy/atlas-main/atlas' + '.asi')".replace("' + '", '')), /Kit\] Release=1/);
  assert.equal(atlasKitProblem("stage('atlas.asi'); ini({ ...ATLAS_KIT_INI })"), '');
  assert.equal(atlasKitProblem("write('atlas.ini', '[Kit]\\nRelease=1')".replace('\\n', '\n')), '');
  assert.equal(atlasKitProblem("{ 'Kit.Release': 1 } for Atlas.ini"), '');
}

if (import.meta.filename === process.argv[1] && process.argv[2] === '--self-check') { selfCheck(); console.log('atlas-kit self-check: 0 FAIL'); }
