// Offline self-check of the far-Sim state machine core (mods/ts3fixes/src/FarSimModel.cs, docs/345 §13.2, docs/354):
// D1 PRNG vector, D2 / D3 trace hash, D4 lazy against stepped, D5 catch-up, D6 line format, D8 version bump, leak churn,
// household rows, shadow table lines; and D9, the static check that the machine source has no random source, no wall
// clock, no object-keyed table and no floating-point field. Windows (.NET Framework csc).
//   node tools/selftest/farsim/run.ts
import { execFileSync, CSC } from '../csc-shim.ts';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..', '..', '..');
const SRC = join(ROOT, 'mods', 'ts3fixes', 'src', 'FarSimModel.cs');

// D9: one regex, tested against lines it must and must not match before it reads the source.
const D9 = /\b(RandomUtil|RandomGen|new\s+Random\b|DateTime|Stopwatch|StopWatch|Environment\.TickCount|Hashtable|float|double)\b|Dictionary<\s*(?!u?int\b|u?long\b|string\b)/;
const d9Cases = {
  match: [
    '            int pick = RandomUtil.GetInt(3);',
    '            int r = RandomGen.NextInt();',
    '        static readonly Random sRng = new Random(7);',
    '            long t0 = DateTime.UtcNow.Ticks;',
    '        static StopWatch sViewerWatch, sViewerClock;',
    '            sIpBeat = System.Environment.TickCount;',
    '        static readonly Hashtable sByDesc = new Hashtable();',
    '            public float Value;',
    '        double mean;',
    '        static readonly Dictionary<Sim, Rec> sBySim = new Dictionary<Sim, Rec>();',
    '        Dictionary<SimDescription, int> m;',
  ],
  noMatch: [
    '            public long EnteredAt, EvalAt, NextDue = -1, FarSince = -1;',
    '        public static uint Range(ulong draw, uint n) { return (uint)(((draw >> 32) * n) >> 32); }',
    '    // clock, no table keyed by an object and no floating-point field.',
    '        static readonly Dictionary<ulong, FsSnap> sFsSnap = new Dictionary<ulong, FsSnap>();',
    '            Dictionary<string, bool> sw;',
    '            readonly List<Rec> mRecs = new List<Rec>();  // sorted by id (H7: never an object-keyed table)',
  ],
};
let bad = 0;
for (const l of d9Cases.match) if (!D9.test(l)) { bad++; console.log(`FAIL D9 regex must match: ${l.trim()}`); }
for (const l of d9Cases.noMatch) if (D9.test(l)) { bad++; console.log(`FAIL D9 regex must not match: ${l.trim()}`); }
const hits = readFileSync(SRC, 'utf8').split(/\r?\n/).map((l, i) => [i + 1, l] as const).filter(([, l]) => D9.test(l));
for (const [n, l] of hits) { bad++; console.log(`FAIL D9 FarSimModel.cs:${n}: ${l.trim()}`); }
console.log(`farsim D9: ${d9Cases.match.length + d9Cases.noMatch.length} regex cases, ${hits.length} hits in FarSimModel.cs`);

const out = join(ROOT, 'build', 'selftest');
mkdirSync(out, { recursive: true });
const exe = join(out, 'farsim-test.exe');
execFileSync(CSC, ['/nologo', '/optimize+', '/platform:x86', '/langversion:5', `/out:${exe}`, join(import.meta.dirname, 'FarSimTest.cs'), SRC], { stdio: 'inherit' });
execFileSync(exe, [], { stdio: 'inherit' });
if (bad) process.exit(1);
