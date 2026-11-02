Thanks. Write-up: docs/209 §#95.

The `[0x11CE404]` correction is verified on the Steam 1.67 exe: `test byte` / `or` of bit 0 at `0x5A4A89` / `0x5A4A92`, only two references, and `0x5AE140` returns `[0x11CE42C]`. docs/190 #21 and docs/193 §3.4 were wrong to call it the cache container; we will correct both and close docs/190 Q2 with your numbers.

Your conclusion matches what we see: with S3PP's STBL layout the per-installed-item records (catalog ~1.2 KB per object, CAS ~0.1 KB per part, STBL ~2.1 KB per table) are not where heavy-CC address space goes, so we will not patch them. These structures sit in Apex Radiance's area anyway.

If you repeat it once with S3PP off, the STBL number would confirm the ~148 MB estimate.
