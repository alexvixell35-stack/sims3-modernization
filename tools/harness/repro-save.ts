// Repro saves: when a run ends with stuck Sims, save the game so a fix can be tested by loading that save instead of
// waiting for the problem to happen again. TS3 resets every Sim on load (interactions and queues are not saved), but
// positions are, so problems tied to a place (a bad spot, a blocked doorway) come back when the Sims retry from there.
// Writes Saves/Repro-<tag>-<HHMM>.sims3 and probe-out/repro/<same name>.json (who was stuck, where, doing what).
import { mkdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

type Ag = (cmd: string, args?: object) => Promise<any>;
const ROOT = resolve(import.meta.dirname, '..', '..');

export async function reproSave(ag: Ag, tag: string, stuck: object[]): Promise<string | null> {
  if (!stuck.length) return null;
  const name = `Repro-${tag}-${new Date().toISOString().slice(11, 16).replace(':', '')}`;
  await ag('autopilot', { enabled: false }); // it would unpause the save
  await ag('set_speed', { speed: 'pause' });
  const r = await ag('save_game', { name });
  if (!r.ok) { console.log(`repro save ${name} not started: ${r.error}`); return null; }
  for (let i = 0; i < 120; i++) { // the save runs as a game task
    const log = await ag('async_log');
    if (log.ok && log.result.some((l: string) => l.includes(`save ${name} -> `))) break;
    await new Promise(res => setTimeout(res, 2000));
  }
  // the agent line comes ~2 s after save_game but the save takes ~11 s and the game does not answer meanwhile
  let answered = false;
  for (let t0 = Date.now(); Date.now() - t0 < 120_000 && !answered;) {
    const st = await ag('game_state');
    answered = !!st.ok && st.result?.save_in_progress !== true;
    if (!answered) await new Promise(res => setTimeout(res, 2000));
  }
  if (!answered) console.log(`repro save ${name}: game did not answer game_state with save_in_progress false within 120 s`);
  mkdirSync(join(ROOT, 'probe-out', 'repro'), { recursive: true });
  writeFileSync(join(ROOT, 'probe-out', 'repro', `${name}.json`), JSON.stringify({ save: name, tag, stuck }, null, 1));
  await ag('autopilot', { enabled: true });
  console.log(`repro save: ${name} (${stuck.length} stuck Sims listed in probe-out/repro/${name}.json)`);
  return name;
}
