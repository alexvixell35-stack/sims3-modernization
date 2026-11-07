# Who holds the part compositors: RcRefCallers on the old save (2026-11-05)

Hardware: AMD Ryzen 9 7945HX 16C/32T, 32 GB, RTX 4070 Laptop 8 GB (driver 32.0.16.2101) + Radeon 610M, Windows 11
Game: Steam TS3W.exe (sha256 b78a3d535535...), LAA on, DXVK, S3SS, S3PP, Apex Radiance 2.18.3
Build: ts3native 0.36.168, local build of `diag/rc-ref-callers` (origin/main f510bc6), release defaults +
`[MemDiag] RcRefCallers=1 RcRefCallersSample=32`
Save: the contributor's old save ("1111 kit5"), autopilot profile, `-disablestoryprogression`, seed 12345, the census setup
Job: `old-rrc-35`, PASS in 40.6 min (35 min of play; 9 `diag: mem` lines, t = 177 s to 2400 s)

## Verdict

The holder is the CAS compositor service registry (`[0x11CE6A0] +0x104`, insert `0x5EECD0`, the kept reference is the
node copy's AddRef at `0x5ED8B8`). It never erases in play, and its growth is the `rcN` growth.

| t (s) | `rcN` | registry entries | registry at 2 refs | `eaRes` (MB) | largest free (MB) |
|---|---|---|---|---|---|
| 177 | 100,895 | 29,089 | 10,332 | 631 | 1,456 |
| 300 | 120,870 | 38,805 | 17,455 | 725 | 1,043 |
| 600 | 147,933 | 59,341 | 34,467 | 809 | 829 |
| 1200 | 180,488 | 90,140 | 59,826 | 872 | 692 |
| 1800 | 205,740 | 115,574 | 83,439 | 873 | 668 |
| 2400 | 221,511 | 135,261 | 105,262 | 874 | 638 |

- t = 300 s to 2400 s: `rcN` +100,641, the registry +96,456 (96% of it). Inserts counted at `0x5ED8B8`: 106,830 since
  the hooks went in; registry erases: 0 (it only grows, entries = the load-time ones + the inserts).
- At the end 105,262 of the 135,261 registry entries (78%) have exactly 2 references: the cache's and the registry's.
  Nothing else holds them. The other 29,999 have a third holder (a SimBuilder part, a cached build, a find in progress).
- `eaRes` stops at ~873 MB from t = 1200 s while `rcN` keeps growing (+41k): from there the cost shows in `rest`, `miCom`
  and the largest free block (1,456 -> 638 MB), not in `eaRes` [measured; why `eaRes` stops is not known].
- Our hook work: 3.8 ms in the last 300 s (6 ns a hooked call, 17 ns a find chain; 0.001% of the interval).

## Per caller (last line, since the start)

| Caller | AddRef | Finds | Release | Net | What |
|---|---|---|---|---|---|
| `0x9EC454` | 1,891,102 | | | +1,891,102 | in `0x9EC420`, a reference-counted pointer copy (EASTL helper, many callers); its Releases are elsewhere |
| `0x9DC52B` | 264,059 | | | +264,059 | |
| `0x5C05D4` | 169,593 | | | +169,593 | paired with the Release at `0x5AA41D` (-169,593) |
| `0x736718` | | 164,023 | | +164,023 | a cache find chain |
| `0x5E306C` | 157,166 | | | +157,166 | the part compositor `0x5E2C10` |
| `0x5EED1E` / `0x5ED8B8` / `0x5EED46` | 106,830 each | | | +106,830 each | the registry insert `0x5EECD0`: two temporaries (released at `0x5EED74` / `0x5EED7D`) and the node copy (kept) |
| `0x5CEB9F` / `0x5D9293` / `0x5CEB7D` / `0x5D92DD` | | | 420,744 / 384,983 / 361,266 / 329,906 | negative | who drops them |

Per-site net is not a leak by itself: AddRef and Release of one reference are often at different sites. The registry
walk is the holder test.

## Per object (1 in 32 new compositors sampled; the table filled, 9,775 not sampled)

1,031 sampled ones alive at net +1 (with the cache's reference: 2), 562 of them in the registry. By last AddRef or find:

| Last AddRef / find | Objects | In the registry | Function |
|---|---|---|---|
| `0xAD42FD` | 292 | 0 | `0xAD4250` (string `Lot/CompositorManager`): a second holder, not the registry |
| `0x5EF61B` | 238 | 238 | strings `CAS/SimService/CachedBuilds`, `CachedBuildListCustom` |
| `0x5E5D45` | 214 | 214 | `0x5E5C90` (strings `CAS/SimOutfitResource/Save`) |
| `0x9EC454` | 168 | 4 | the pointer copy above |
| `0x5ED8B8` | 70 | 70 | the registry node copy |
| `0x5E03D4` | 31 | 28 | |

The last AddRef is not always the holder's: registry members are often AddRef'd and released again later (cached builds,
outfit save), so their last AddRef is that code. The `Lot/CompositorManager` group (292 sampled, none in the registry) is
the next lead after the registry.

## Next

- Step 2: `[CasTrim]` (branch `fix/cas-registry-trim`, stacked on this one) erases registry entries at exactly 2
  references and unused for 30 min, with EA's own erase `0x5EE640`; a count-only mode first.
- `Lot/CompositorManager` (`0xAD4250`): what it keeps, after the registry.
