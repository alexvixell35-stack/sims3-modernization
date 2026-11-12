// Forced repro of the Sim thumbnail stall ([ThumbStall], thumbstall.cpp, issue #40). Game speed ultra, then N rounds of:
// thumb_outfits (portraits of townies' unworn outfits, no wait) and thumb_census samples every 3 s for the round. One line
// per round and a summary: samples with the front request in state 0x11, the longest front wait, portraits done. Household
// Sims untouched (thumb_outfits skips the active household). Exit 0 always: the numbers are the result.
// Baseline-BP, 2026-11-12: [ThumbStall] Action=0 (log only) 57% of samples in 0x11, longest 48.6 s, done +87;
// Action=3 TimeoutMs=3000: 3%, 3.1 s, done +178.
//   node tools/harness/thumb-outfits.ts [--rounds 6] [--roundS 30] [--sims 20] [--perSim 4]
//   node tools/harness/thumb-outfits.ts --self-check
import assert from 'node:assert/strict';

const argv = process.argv.slice(2);
const num = (a: string[], k: string, d: number) => { const i = a.indexOf(k); const v = i >= 0 ? Number(a[i + 1]) : d; return Number.isFinite(v) && v > 0 ? v : d; };
if (argv.includes('--self-check')) {
  assert.equal(num(['--rounds', '3'], '--rounds', 6), 3);
  assert.equal(num([], '--rounds', 6), 6);
  assert.equal(num(['--rounds', 'x'], '--rounds', 6), 6);
  assert.equal(num(['--rounds'], '--rounds', 6), 6);
  console.log('thumb-outfits self-check ok');
  process.exit(0);
}
const rounds = num(argv, '--rounds', 6), roundS = num(argv, '--roundS', 30), sims = num(argv, '--sims', 20), perSim = num(argv, '--perSim', 4);
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const ag = async (c: string, args?: object): Promise<any> => {
  const q = new URLSearchParams({ cmd: c, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await (await fetch(`http://127.0.0.1:47891/agent?${q}`, { signal: AbortSignal.timeout(20000) })).json(); } catch (e) { return { ok: false, error: String(e) }; }
};

console.log(`${stamp()} thumb-outfits: speed ${JSON.stringify((await ag('set_speed', { speed: 'ultra' })).ok)}`);
await ag('thumb_census', { reset: true });
let all = 0, in11 = 0, maxAge = 0, asked = 0;
const done0 = (await ag('thumb_census', {})).result?.tm_done ?? 0;
for (let r = 1; r <= rounds; r++) {
  const t = await ag('thumb_outfits', { sims, perSim });
  asked += t.result?.asked ?? 0;
  let n = 0, s11 = 0, age = 0;
  for (const end = Date.now() + roundS * 1000; Date.now() < end; await sleep(3000)) {
    const c = (await ag('thumb_census', {})).result;
    if (!c || c.native !== 'ok') continue;
    n++;
    if (c.tm_front_state === 0x11) s11++;
    age = Math.max(age, c.tm_front_age_ms ?? 0);
  }
  all += n; in11 += s11; maxAge = Math.max(maxAge, age);
  console.log(`${stamp()} thumb-outfits: round ${r}: asked ${t.result?.asked ?? JSON.stringify(t.error ?? t)} for ${t.result?.sims ?? '?'} Sims; front in 0x11 ${s11}/${n} samples; longest front ${(age / 1000).toFixed(1)} s`);
}
const done1 = (await ag('thumb_census', {})).result?.tm_done ?? 0;
await ag('set_speed', { speed: 'normal' });
console.log(`${stamp()} thumb-outfits: summary: asked ${asked}, done +${done1 - done0}, front in 0x11 ${in11}/${all} samples (${all ? Math.round(100 * in11 / all) : 0}%), longest front ${(maxAge / 1000).toFixed(1)} s`);
