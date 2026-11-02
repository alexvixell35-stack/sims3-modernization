Thanks, this answers several questions we had only inferred. Write-up: docs/209 §#96.

What we take from it:
- EA already streams render data per active lot; the lever left is the ~180 MB camera-independent texture set, owners still open.
- Under DXVK, `rc` is not address space. We will note that in docs/40 and docs/201 and stop reading `rc` as Error 12 pressure on DXVK installs.
- EA heap cores are live data, not hoarding. We will add EA-core reserved/committed to `[MemDiag] Light`, as you suggest (two dword reads per core), so they no longer hide in `rest`.

Texture streaming, mip drops and the cache itself are Apex Radiance's area, so we pass the camera-independent set and the 2048-class load-time mip drop idea (docs/40 E2) to them.

If you get to it: naming the owners of the 53 large camera-independent textures (Sims, terrain, sky/water, UI) would decide whether a streamer is worth building.
