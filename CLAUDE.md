# Sims 3 modernization

Make The Sims 3 run well on modern hardware: fewer crashes (Error 12, crash on save), less stutter, faster loads, and room for heavy mod setups. First collate what existing community fixes already do, then reverse engineer the game and build our own fixes (binary patches and DLL hooks) where those fall short.

## Setup this work targets
- The Sims 3, Steam edition, all expansions and stuff packs
- Windows 11
- Alienware m15 R6: Intel i7-11800H (8 cores / 16 threads, 11th gen, so no Alder Lake fix needed), 32 GB RAM, RTX 30-series laptop GPU
- Game exe: `<Steam library>\steamapps\common\The Sims 3\Game\Bin\TS3W.exe`

## Hard constraints
- **TS3W.exe is 32-bit.** With Large Address Aware it gets 4 GB of address space at most, no matter how much RAM is installed. We cannot raise that without a 64-bit rebuild; we can only waste less of it (smaller stacks, leaner caches and allocators, textures moved out of process via DXVK). Extra RAM still helps through the Windows file cache.
- The simulation (Mono script VM, interaction scheduler, pathfinding) is mostly single-threaded. Gains come from making the main thread cheaper per tick and moving side work to other cores, not from parallelizing the sim outright. Long-term goal: move all work that can safely leave the sim thread to other cores; offload changes are accepted with no gain or up to ~2% loss if stable (PLAN.md Goal, docs/23 #17).
- Always keep `TS3W.exe.orig` and back up `Documents\Electronic Arts\The Sims 3\` before testing a patch.

## Tooling decisions
- **Static analysis:** REA, Reverse Engineer Anything (https://github.com/morluto/rea), an MCP server and CLI that wraps Ghidra/IDA plus static .NET analysis. Use it with Ghidra on copies of TS3W.exe and the Mono script DLLs. It has no Windows runtime tracing.
- **Live profiling and debugging:** x64dbg (x32dbg for this 32-bit exe) or Frida.
- **DLL hook reference:** Sims 3 Settings Setter (https://github.com/sims3fiend/Sims3SettingsSetter), an ASI plugin with assembly patches for stacks, WorldCache, mimalloc, GC throttling, tick limiting and lot streaming. Study it before writing our own hooks and avoid duplicating what it already does.
- Baseline stack: DXVK x32 `d3d9.dll` (has a built-in TS3W profile) + S3SS, optionally the Sims 3 Performance Patch (Nexus mod 235).

## Status
See `PLAN.md` ("Where things stand" and "Next tasks"): it is the source of truth after a context reset. Priority: in-game stutter.

## Repository
- **Published on GitHub** for contributors (`README.md`, `AGENTS.md` for agent rules, branch and PR conventions). Never commit EA game files, decompiled code, saves, dumps or captures, and no personal paths, machine names or emails.
- `.gitignore` keeps EA game files (exe, dll, package), Ghidra/REA projects, dumps, traces and build output out of git. Keep game copies for analysis in `game-copy/`, which is ignored. Commit notes, findings, scripts and our own patch source only.

## Docs
- `docs/index.md`: one line per doc (what it covers, key addresses, switches, queues). Grep it (or `docs/`) for a topic, address or switch, then read only the matching doc. New docs get their index line there, not here.
- Always relevant: `PLAN.md` (resume here), `docs/04-re-findings.md` (verified findings), `docs/51` (inference ledger), `docs/registry.md` (counter slots), `CONTRIBUTING.md`, `docs/23-ideas-and-roadmap.md`.

## Working style
- The main agent orchestrates; triage, static analysis, fixes and queue/doc work go to sub-agents (`AGENTS.md` Rules, "Main agent orchestrates").
- Rodney wants every avenue tried, including binary patches and DLL hooks.
- Mark claims as verified, community-reported, or inferred, and confirm inferred ones in the disassembly or by measurement.
- Change one thing at a time and compare against the baseline table.
- When a new, potentially better design exists for a feature, test it first; drop pending timing runs of older designs once the new one shows big gains (keep the old design only as a ladder baseline arm).
- Before any timing queue over ~1 h, a short smoke job must prove the measurement works (per-arm split, heavy tools off, switch values read back); queue.ts runs it automatically.
- Effort estimates (task tables, designs, docs) are wall-clock time for Claude with agents and Rodney's help, in the smallest fitting unit: minutes, hours, or days only for work that really takes 24 h or more. Base them on measured pace (an agent build is ~10-60 min, a live job ~8-15 min) and list game runs and clean nights separately. Older docs that say "N d" used a much coarser unit (0.5 d was often ~30 min of work).
