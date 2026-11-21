# 683. Load crash of run s194: the test rig's /tmp quota ran out (not a game or mod bug)

2026-11-20, static: run log, probe files, system journal, the other agents' command results of the same minute,
disassembly of the Wine `ucrtbase.dll` and the `d3d9.dll` of the rig, project symbol tables. No game run.
Tags as in docs/51: [verified] = read in a log, a file or the disassembly; [inferred] = follows from verified facts.

## 0. Verdict

- **Not ours, not DXVK, not S3SS, not EA: a rig failure.** The game died because the Linux test rig's `/tmp` (a tmpfs with a
  per-user quota) was over quota for about 10 seconds while other work filled it. Wine keeps the pages of unnamed file
  mappings in files under `/tmp`, so a first write to a fresh page of DXVK's texture memory could not get a page and the
  game got an access violation on valid memory. [quota state verified, the kernel and Wine step inferred, §3]
- 1 of 8 loads on the build, 6 clean retries with fixes on and off: this fits a cause outside the game. [verified counts]
- The waypoint area cache race (`W+0xE0`, docs/679, docs/670) has nothing to do with it: the fault is on the main thread
  inside a texture create, no routing code is on its stack, and the sim thread was reading lot objects. [verified, §1]
- **Correction to the run notes:** this rig does not run Apex or the RTX Remix bridge. `d3d9.dll` is plain DXVK 2.7.1
  x32 in `system32`, the log says `compat: Apex Radiance not loaded at start`, and the runtime is Wine 11.6 (TkG, staging,
  NTSync), not Proton. [verified: file strings, log line 7, journal]
- Built: `tools/harness/tmp-headroom.ts` (one line, exit 2 when room is low) for the rig scripts. No change to ts3native
  or ts3fixes.

## 1. What faulted

Log `ts3native-full.log` of the run (756 lines), main thread 232, right after the `LoadWorld` handler ran:

| # | Line | Fault | What it is |
|---|---|---|---|
| 1 | 733 | C0000005 write `0x621A7000` at `ucrtbase+0x61AD0` | `memset` main loop (32 bytes a step, `ecx` = cursor), `eax` = 0 |
| 2 | 753 | C0000005 write `0x62E3BA30` at `ucrtbase+0x61A2B` | `memset` writing the last 16 bytes of a 0xC000-byte block |
| 3 | 755 | C0000005 read `0x8BF18B6A` at `Sims3SettingsSetter+0x4E313` | same address, registers and stack words in 8 other run logs, each time as the last line: S3SS's own code at process death [inferred] |

`ucrtbase+0x61AD0` and `+0x61A2B` are both inside the export `memset` (`+0x619F0`) of the rig's Wine `ucrtbase.dll`
[verified: export table and disassembly]. Fault 1 was swallowed by a handler up the stack (the main thread kept presenting
for at least 0.6 s, lines 735-752); fault 2 was fatal [verified sequence; which handler is unknown].

Fault 1 in numbers [verified from the registers]: destination start `ebx` = `0x61F2A4C0`, loop end `esi` = `0x6247FA20`,
so the length is 0x555560..0x55557F bytes: a 1024 x 1024 32-bit texture with its full mip chain (4 x 1,398,101 = 0x555554
plus per-level rounding). `memset` writes the first and last 32 bytes before the loop, so the block's head and tail pages
were writable; 0x27CB40 bytes (647 pages) went through; the fault is the first write to page `0x621A7000`.
Fault 2: block `0x62E2FA40`, 0xC000 bytes; the first 16 bytes were written, the write to its last 16 bytes on page
`0x62E3B000` faulted.

Call path [verified: stall-watch samples of lines 708 and 711 show the same path without a fault, the return addresses
follow CALLs in the DXVK file, names from `tools/ghidra/symbols`]:

- TS3W: `TextureResource_Load 0x723270` -> `DDS_LoadTexture` (ret `0x60E1E1`) -> `Texture_CreateByShape` (ret `0x60CF29` /
  `0x60CEFC`) -> `Texture_Create2D` (ret `0x619CEB`, fault 1) or `Texture_CreateCube` (ret `0x61996B`, fault 2) ->
  `Texture_MipCount` (ret `0x619841`) -> `Texture_CreateD3D` (ret `0x619658` / `0x619625`).
- S3SS's device wrapper (`+0x322EF`, `+0xD6A2D`), a pass-through here.
- DXVK: `CreateTexture` (ret `d3d9+0x2C58F`) -> texture initializer `d3d9+0x53C00` (ret `+0x53CCE`) -> `d3d9+0x53580`,
  which ends in `memset(dst, 0, [texture+0x490])` (ret `+0x536ED`; the callee is the import thunk of `memset`).
  In DXVK 2.7.1 this is the zero fill of a new texture's system-memory copy; on 32-bit builds that copy lives in
  DXVK's D3D9 memory allocator, which takes 64 MB sections from `CreateFileMappingA(INVALID_HANDLE_VALUE, ...)` and maps
  parts of them with `MapViewOfFile` [imports verified in the file; the allocator's layout is from the DXVK source, inferred
  for this binary]. The stack words `0x00555570` and `0x0055FA40` that the crash line prints as "code refs" are this
  block's size and its view size, not code [inferred: they match the lengths above].

The other two stack words of line 733 (`0x60D91C` in `DDS_UploadLevels`) are older frames left on the stack.
`ts3native+0x5CB06` in the stall-watch scans is our lock detour's return address left below the stack top from earlier calls
(`Lock_Acquire` next to it); it is not a frame of the fault [verified: not in either fault's reference list].

## 2. Ours ruled out

- No ts3native frame between `Texture_CreateD3D` and the fault; the write is DXVK's, to DXVK's own fresh block. [verified]
- ts3native has no hook on file mappings in this configuration, and every `VirtualProtect` in the non-test source goes to
  code or tables at fixed addresses (exe sites, our own pages) [verified: grep of `native/ts3native`, 2026-11-20; AllocSites
  and MemDiag, which do hook the virtual-memory calls, were off in this run].
- 3 retries with all fixes on and 3 with them off were clean, and no other log of the rig (945 run folders) has an
  exception in `ucrtbase`. [verified grep]

## 3. Cause: /tmp over quota in the same seconds

Timeline (UTC, rig clock) [verified]:

| Time | Event | Source |
|---|---|---|
| 16:05:32 | game start | journal |
| 16:06:07 | "Mod Scripts Found" answered, save list read | autotest summary |
| 16:06:16 | last probe memory sample | `memory.csv` |
| 16:06:17, :22, :25 | commands of two other agents on the rig fail: a test write under `/tmp` returns EDQUOT ("Disk quota exceeded") | their tool results |
| 16:06:25 | probe: game process gone | `events.jsonl` |
| 16:06:27 | one of them deletes a 628 MB Wine prefix from `/tmp` | its command |
| 16:06:30 | autotest: "game died while waiting for world load" | summary |
| 16:06:32 | `df`: `/tmp` 12G used, 3.7G free | its tool result |

Facts about the rig [verified 2026-11-20]:

- `/tmp` is a 15.3 GiB tmpfs mounted by systemd with `usrquota`; `quotactl_fd` reports a hard limit of 12,847,190 KiB
  (12.25 GiB, 80% of the volume) for the user. So the volume still had 3.7 GiB free while the user could not add a page.
- 10.4 GiB of it is the coding agents' scratch directory (about ten Wine prefixes of 615-660 MB each, work trees, Ghidra
  projects).
- The game's Wine puts unnamed mappings in `/tmp/.wine-<uid>/server-*/tmpmap-*` (deleted after creation, mapped shared):
  a running game shows 50+ such views in `/proc/<pid>/maps`. They belong to the same user, so their pages count against
  the same quota.

Mechanism [inferred from the kernel's and Wine's behaviour, not observed in a capture]: the mapping file is sized with
`ftruncate`, so tmpfs gives it pages only on first touch. A first touch that cannot be charged (volume full or over
quota) ends in SIGBUS, not in an out-of-memory kill. Wine's 32-bit signal handler takes SIGBUS through the page-fault
path, finds the page committed and writable in its own tables and hands the program `STATUS_ACCESS_VIOLATION` with the
write flag: the two faults of §1. Parts of the block that an earlier, freed texture had used already own their pages and
stay writable, which is why 647 pages and the head and tail went through and one page in the middle did not.

What this explains that the alternatives do not: two faults 0.6 s apart at unrelated addresses, each on a single page of a
block whose other pages took writes in the same call. A stale unmap would remove a whole view; a protection change by a
hooking mod would need two stale targets inside two new DXVK blocks within a second, in the first load of the process.

Open: no capture shows the SIGBUS itself (`WINEDEBUG` is off, `DXVK_LOG_LEVEL=none`), so the last step stays inferred
until §5's check is run.

## 4. Side finding: address space on this rig

The probe's `memory.csv` shows the same picture in every load of the day, crashed or not: by the time the world loads,
free address space in the Wine-managed area is 35-60 MB and the largest free block 15.9 MB (commit 1.7-1.9 GB, another
2.2-2.3 GB reported as reserved, which on Wine is mostly the range outside its own reserve) [verified numbers; reading of
the reserved figure inferred]. Not the cause here (both blocks were mapped), but a load that needs one block above 15.9 MB
in that area depends on Wine finding room outside it.

## 5. What to do on the rig

1. Keep `/tmp` below its quota while a game runs: move the agents' temp directory to the home volume
   (`CLAUDE_CODE_TMPDIR`), or keep Wine prefixes and work trees of side jobs out of `/tmp`, and delete the stale
   prefixes (about 6.6 GiB today). Raising or dropping the quota (`usrquota` option of `tmp.mount`) needs root.
2. In `autotest.sh` (rig script, not in the repo), before each game start and after every "game died":
   `node tools/harness/tmp-headroom.ts --min-mb 2048 | tee -a "$OUT/summary.txt"`. Exit 2 before a start = wait or clean
   first; a `LOW` line after a death = record the cycle as a rig failure (N/A), not as a crash of the build.
3. Proof run, once, on a quiet rig (2 load-only cycles, ~5 min each): (a) fill `/tmp` to within 1 MB of the quota with
   `fallocate` during the main menu, start the load: the same `memset` fault under `d3d9` is expected; (b) same without
   the fill: clean. `[Crash] Log=1` (already on in every build) is enough; add `WINEDEBUG=+seh,+virtual` for the (a)
   cycle to see the fault's signal in Wine's own trace. No crash-forensics guards are needed: nothing in the game's heap
   is wrong.
4. Triage rule for the crash ledger: `C0000005` write in `ucrtbase.dll` `memset` on the main thread with `d3d9` texture
   create on the stack, on the Linux rig = check the temp volume first.

## 6. Note for the owners of the other parts (nothing to file)

DXVK, S3SS and the game behaved correctly for the memory they were given. The only upstream-facing remark would be for
Wine: a file-backed page that cannot be allocated reaches the program as a plain access violation with no message when
`WINEDEBUG` is off. Not worth a report from us; the fix is on the rig.
