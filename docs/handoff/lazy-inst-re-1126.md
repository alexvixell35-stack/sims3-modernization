# Handoff: lazy instantiation RE (docs/344, phase R lane 3 of docs/340)

Written 2026-11-26. Static only, docs only, on master. Nothing built, nothing run. Ledger: docs/51 §613.
For: RX-9 (verdict), SD-4 (proxy spec), LZ-1 (probe), RX-2a / RX-2b (query census), and whoever builds FS-16..19.

## What is answered

| docs/340 item | Answer | Where |
|---|---|---|
| RX-3: create path, where the id is allocated | Script create: `0x76D0E0` -> host -> `0x937F60` -> `0x93A1E0`; id from `ReserveId 0x9399F0` (random when zero) | docs/344 §2.2 |
| RX-3: can an object be created with a chosen id? | Natively yes (last two arguments of `0x93A1E0`, `0x937F60`, `0x9376D0`); from script no (the host pushes 0, 0 at `0xD7BAC8`) | docs/344 §2.2, V-613-1, V-613-2 |
| RX-3: which native parts can be released while the script object lives? | None: one allocation per object, components inside it | docs/344 §2.1, V-613-4 |
| RX-4: are ids stable over save / load? | Yes: stored in the lot object list, passed back to `ReserveId` at load | docs/344 §4, V-613-3 |
| RX-4: can lot contents be skipped at load (L3)? | No inside a `.sims3`: one script stream, global indices in lot and object records | docs/344 §4, V-613-6, I-613-3 |
| RX-2a, object half | Choke points `0xD798F0` (all `Query_*`) and `0x76CA60` (id -> proxy); call-site counts per query family; top types | docs/344 §3 |
| RX-9 draft | L0 go, L1 no-go, L2 objects no-go on EA worlds (pilot on WS-0 only), L2 Sims go first, L3 no-go | docs/344 §1 |

## The four findings SD-4 and RX-9 should start from

1. **L1 has nothing to strip.** The plan's "script object stays, native model / rig / footprint released" has no
   supported path: the components are inside the object's one block and EA streaming already removes the scene nodes
   of inactive lots. Drop L1 as a memory stage; keep its idea only as "skip parked objects in our own walks".
2. **A save contains only what is in memory.** Any L2 design must materialise everything before a save. Then no
   sidecar is needed for L2 at all, and the save is a normal EA save.
3. **The managed half returns no address space on EA worlds** (Boehm heap is kept; 151 MB of it is free). Object L2
   can only pay on a world several times larger than EA's.
4. **Far Sims are the better target**: EA already runs residents without a Sim object, and one Sim is worth 30-70
   objects in memory.

## Not done (pick up here)

- Z0 / LZ-1 probe: bytes per object and per class, inbound references, far share. All of docs/344 §5 is an estimate.
- Per-site classification of the 297 world-wide query sites (count / exists / nearest / iterate; timer or one-shot).
  Only type counts are done.
- RX-2b (mods), RX-6 (readers that need a Sim object).
- The native drain of the pending (id, index) list and the caller of `BindLoadedObject` (open since docs/50 §8).
- Writers of byte `0x11E90F4` (I-613-5).
- Instruction-level read of `0x937F60` (arguments, `ret`) before any call from our code; `node tools/hook-sites.ts
  --check` and docs/68 before any hook on `0xD798F0` or `0x76CA60`.
- EA 1.69 addresses.

## Files

- `docs/344-lazy-instantiation-re.md`, docs/51 §613, index line.
- Local only (ignored): `game-copy/analysis/decomp/lazy/lz1.c` (17 functions: `0x93A1E0`, `0x9399F0`, `0x76D0E0`,
  `0x939B60`, `0x939870`, `0x749630`, `0xD798F0`, `0x950280`, `0x9501B0`, `0x94FCE0`, `0x769B80`, `0xC70320`,
  `0x9361D0`, `0x94F1C0`, `0x937F60`, `0x9376D0`, `0x939680`).
- Tool note: the REA MCP server timed out on its own Ghidra start while two other lanes ran; the decompiles came from
  `tools/ghidra/run-headless.sh DecompileTo.java` on the read-only project, which runs fine beside another lane.
