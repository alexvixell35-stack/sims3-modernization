// Known EA script errors (known-script-errors.json): matches a ScriptError_*.xml / ErrorTrap report by exception type +
// the top stack frames in order (+ optional `message` regex on the exception message). Only all-EA stacks can match: one
// frame from ts3fixes, ts3agent, NRaas or any other mod and the error is never known, except frames the entry lists in
// `passFrames` (a verified pass-through hook of ours), which are dropped before matching. Callers report matches as 'known EA error <id> (ignored)' and count them.
//   node tools/harness/known-script-errors.ts <ScriptError.xml>...   print the known id (or 'unknown') per file
//   node tools/harness/known-script-errors.ts --self-check
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export type KnownError = { id: string; exception: string; frames: string[]; message?: string; passFrames?: string[]; why: string; first_seen: string; evidence: string[] };
const LIST = join(dirname(fileURLToPath(import.meta.url)), 'known-script-errors.json');
export const loadKnown = (path = LIST): KnownError[] => JSON.parse(readFileSync(path, 'utf8')).errors;

// EA game script namespaces (Sims3GameplaySystems/Objects, SimIFace, UI, ScriptCore, mscorlib)
const EA = /^(Sims3\.Gameplay|Sims3\.SimIFace|Sims3\.UI|ScriptCore|System)\./;

// '#0: 0x0003b callvirt   in Sims3.Gameplay.Situations.Sims3.Gameplay.Situations.WeddingCeremony:CleanUp () ()'
export function parseScriptError(xml: string): { exception: string; message: string; frames: string[] } {
  const ex = xml.match(/^\s*([\w.]+(?:Exception|Error))\s*:[ \t]*(.*)$/m);
  const frames = [...xml.matchAll(/^#\d+:\s*0x[0-9a-f]+\s+(?:\S+\s+)?in\s+([^\s(]+)/gim)].map(m => m[1]);
  return { exception: ex?.[1] ?? '', message: (ex?.[2] ?? '').trim(), frames };
}

export function matchKnown(xml: string, known: KnownError[] = loadKnown()): KnownError | undefined {
  const { exception, message, frames } = parseScriptError(xml);
  const is = (f: string, want: string) => f === want || f.endsWith('.' + want);
  return known.find(k => {
    const fs = frames.filter(f => !k.passFrames?.some(p => is(f, p)));
    return fs.length > 0 && fs.every(f => EA.test(f)) && k.exception === exception && (!k.message || new RegExp(k.message).test(message))
      && k.frames.length <= fs.length && k.frames.every((w, i) => is(fs[i], w));
  });
}

// only when run directly: save-matrix.ts imports this module
const argv = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url) ? process.argv.slice(2) : [];
if (argv[0] === '--self-check') {
  const assert = (await import('node:assert')).strict;
  const known = loadKnown();
  assert.ok(known.every(k => k.id && k.exception && k.frames.length && k.why && k.first_seen && k.evidence.length), 'every entry has id, exception, frames, why, first_seen, evidence');
  assert.equal(new Set(known.map(k => k.id)).size, known.length, 'unique ids');
  const fr = (n: string, op = 'callvirt') => `#X: 0x0003b ${op}   in ${n} () ()`;
  const wed = [
    'Sims3.Gameplay.Situations.Sims3.Gameplay.Situations.WeddingCeremony:CleanUp',
    'Sims3.Gameplay.Autonomy.Sims3.Gameplay.Autonomy.Situation:Exit',
    'Sims3.Gameplay.Situations.Sims3.Gameplay.Situations.WeddingCeremony+RouteToWeddingArch:OnRouteFailed',
    'Sims3.Gameplay.ActorSystems.Sims3.Gameplay.ActorSystems.InteractionQueue:DeQueue',
    'Sims3.Gameplay.Actors.Sims3.Gameplay.Actors.Sim:Simulate',
    'ScriptCore.ScriptCore.ScriptProxy:Simulate',
  ];
  const xml = (ex: string, frames: string[]) => `<NRaas.ErrorTrap>\n<Content>\n Lot Name: Sparkling Sands\n\n${ex}: A null value was found where an object instance was required.\n${frames.map((f, i) => fr(f, i === 1 ? '' : 'callvirt').replace('X', String(i))).join('\n')}\n\nLoaded Assemblies:\n Ts3Fixes\n NRaasErrorTrap: Version 100\n</Content>\n</NRaas.ErrorTrap>`;
  const nre = 'System.NullReferenceException';
  assert.deepEqual(parseScriptError(xml(nre, wed)).frames, wed);
  assert.equal(matchKnown(xml(nre, wed), known)?.id, 'ea-wedding-cleanup-nre', 'EA-only wedding stack is known');
  assert.equal(matchKnown(xml(nre, [...wed.slice(0, 4), 'Ts3Fixes.Ts3Fixes.Hooks:Run', ...wed.slice(4)]), known), undefined, 'a ts3fixes frame never matches');
  assert.equal(matchKnown(xml(nre, [...wed, 'NRaas.NRaas.ErrorTrap:Wrap']), known), undefined, 'an NRaas frame never matches');
  assert.equal(matchKnown(xml('System.InvalidCastException', wed), known), undefined, 'other exception type is not known');
  assert.equal(matchKnown(xml(nre, [wed[1], wed[0], ...wed.slice(2)]), known), undefined, 'frame order matters');
  assert.equal(matchKnown('<Content>Destroyed Object Found: Glass</Content>', known), undefined, 'no stack, not known');
  // household book carry error: real frames (probe-out 2026-10-11 all EA; 2026-11-12 through BookHandQueueHook)
  const ae = 'System.ApplicationException';
  const book = "Lala Angelista: Interaction Sims3.Gameplay.Objects.Bookshelf_ReadSomething left an object in the Sim's hand with no carry state machine: Sims3.Gameplay.Objects.BookGeneral";
  const plate = "Lala Angelista: Interaction Sims3.Gameplay.Objects.Plumbing.Sink+WashDishes left an object in the Sim's hand with no carry state machine: Sims3.Gameplay.Objects.CookingObjects.Plate";
  const iq = 'Sims3.Gameplay.ActorSystems.Sims3.Gameplay.ActorSystems.InteractionQueue:';
  const tail = [iq + 'ProcessOneInteraction', 'Sims3.Gameplay.Actors.Sims3.Gameplay.Actors.Sim:DoInteraction', 'Sims3.Gameplay.Actors.Sims3.Gameplay.Actors.Sim:Simulate', 'ScriptCore.ScriptCore.ScriptProxy:Simulate'];
  const eaBook = [iq + 'OnError', iq + 'PutDownCarriedObjects', ...tail];
  const hookBook = [iq + 'OnError', iq + 'PdcoOrig', 'Ts3Fixes.Fixes+BookHandQueueHook:PutDownCarriedObjects', ...tail];
  const bx = (msg: string, frames: string[]) => xml(ae, frames).replace(`${ae}: A null value was found where an object instance was required.`, `${ae}: ${msg}`);
  assert.equal(parseScriptError(bx(book, eaBook)).message, book, 'message parsed');
  assert.equal(matchKnown(bx(book, eaBook), known)?.id, 'ea-book-carry-no-statemachine', 'all-EA book carry stack is known');
  assert.equal(matchKnown(bx(book, hookBook), known)?.id, 'ea-book-carry-no-statemachine', 'stack through the BookHandQueueHook pass-through is known');
  assert.equal(matchKnown(bx(plate, eaBook), known), undefined, 'same stack, Plate (not BookGeneral) is not known');
  assert.equal(matchKnown(bx(book, [...hookBook, 'NRaas.NRaas.ErrorTrap:Wrap']), known), undefined, 'a mod frame outside passFrames never matches');
  assert.equal(matchKnown(xml(nre, [...wed.slice(0, 4), 'Ts3Fixes.Fixes+BookHandQueueHook:PutDownCarriedObjects', ...wed.slice(4)]), known), undefined, 'passFrames apply only to their own entry');
  console.log('known-script-errors self-check: PASS');
} else if (argv.length) {
  const known = loadKnown();
  for (const f of argv) console.log(`${f}: ${matchKnown(readFileSync(f, 'utf8'), known)?.id ?? 'unknown'}`);
}
