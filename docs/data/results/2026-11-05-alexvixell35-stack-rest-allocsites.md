# What is `rest`? AllocSites on the old save (2026-11-05, compositor task step 4)

Hardware: AMD Ryzen 9 7945HX 16C/32T, 32 GB, RTX 4070 Laptop 8 GB (driver 32.0.16.2101) + Radeon 610M, Windows 11
Game: Steam TS3W.exe (sha256 b78a3d535535...), LAA on, DXVK, S3SS, S3PP, Apex Radiance 2.18.3
Build: ts3native 0.36.168, local build of `fix/cas-registry-trim` (origin/main f510bc6 + RcRefCallers + CasTrim, both off),
release defaults + `[MemDiag] AllocSites=1 AllocSitesSample=64 AllocSitesTop=12`, `[Compat] ApexStandDown=0` (so AllocSites
runs beside Apex; measuring only)
Save: the contributor's old save ("1111 kit5"), autopilot profile, `-disablestoryprogression`, seed 12345, the census setup
Job: `old-allocsites-35`, PASS in 40.9 min (9 `diag: mem` lines, t = 177 s to 2400 s)

## Fields, t = 300 s to 2400 s

| Field | Change | End |
|---|---|---|
| `priv` | +460 MB | 2,546 |
| `eaRes` | +201 MB | 910 |
| `rest` | +237 MB | 963 |
| `eaDir` | +15 MB | 293 |
| `gcHeap` | +8 MB | 368 |
| `rcN` | +122,164 | 240,680 |
| largest free | -495 MB | 562 |

`rest` = `priv` minus `eaRes`, `gcHeap`, `miCom` and `eaDir`: private memory that is not EA's heap, Mono's GC heap or EA's
direct blocks.

## `rest` by site (AllocSites `grow` rows, samples #2 -> #9)

| Owner | Site | Growth | Note |
|---|---|---|---|
| **Frida agent (the test harness)** | `frida-agent.dll+0x6E917` (two callers) | **about +123 MB** | steady, 3-4 MB/min, almost never freed (allocated 116 MB / freed 4 MB at one caller); 178 MB live at the end |
| Apex Radiance (its own) | `AcLayers.DLL+0x32736` < `ApexRadiance.asi+0x3FB86B`, `ApexRadiance.asi+0xBC976` < `AcLayers.DLL+0x32736` | about +72 MB | high churn (16.7 GB allocated / 16.7 GB freed at the first) |
| the rest | untracked private regions (thread stacks and others: 84 regions / 317 MB at the end), small sites | about +40 MB | |

The `grow` rows are the top 12 per interval, so each number is a lower bound for that site; churning sites (d3d9 file views,
CAS geometry heap blocks) also show positive rows that their frees cancel (views: -3 MB net over the run).

Not `rest`, for reference: `ApexRadiance.asi+0x8AD68` < `0x004E4E87` (EA heap new core) +112 MB and the same Apex
function from EA's direct-block and core paths: Apex allocates EA's heap cores, so that growth is `eaRes` / `eaDir`.

## Verdict

- **About half of `rest`'s growth is our own test harness:** the probe's Frida agent grows by 3-4 MB a minute, steadily,
  and is never freed. Players don't run it. Every harness run with the probe attached has it, so the census runs' `rest`
  (+323 MB in 60 min) very likely includes about 200 MB of it [inferred: same probe, same setup; this run measured it].
- About a third is Apex Radiance's own memory (through the Windows compatibility shim `AcLayers.DLL`). That is Apex's area:
  measured only, left to its author.
- `gcHeap` does not grow here (+8 MB), so no `gcHeap` breakdown.

## Next

- Find what the Frida agent allocates (the probe's `agent.js`: per-call `Memory.alloc` or message buffers are the first
  suspects) and stop it, so harness runs measure the game. Until then, subtract the `frida-agent.dll` rows from `rest`.
