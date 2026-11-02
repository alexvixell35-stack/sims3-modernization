# 234. EA 1.69 product/entitlement gate: live diagnosis (BaseGame-only flags)

EA laptop, EA app 1.69.47 (`Sims3Launcher.exe` = TS3.exe, base 0x400000, no ASLR), 2026-11-02 13:09-13:20 PHT. Question: do the
19 installed packs stay unmounted because the entitlement answer arrives after the gate (timing), or because the game does not
match what the EA app answers (data)? Static context: mount at 0x596A70, gate 0x596990 -> service create + wait 0xA42A30, loaded test
0xA42080, fields `[0x1244FB0]+0x28` (answered) / `+0x24`, flags dwords `[0x1227E50]`, `[0x1227E54]`. Player thread:
https://forums.ea.com/discussions/the-sims-3-technical-issues-en/all-19-owned-packs-installed-but-game-loads-base-game-only-sunset-valley/13626580

## Runs (times PHT; t = seconds after the game process appeared)
| Run | Method | Result |
|---|---|---|
| A 13:09:36 | Frida attach 0.5 s after start, `tools/probe/ep-gate-probe.js` (hooks + 100 ms reads) | .text decrypted at t=0.6; service pointer, flags stayed 0 for 60 s; no LSX traffic in the EA app log; game exited by itself at t=63 s, EA app: `Game process exit code=[-1]`, `Game session was too short: [63] seconds`. [verified] Early Frida attach is not usable on the EA build (cause inferred: DRM). |
| B 13:15:17 | OpenProcess + ReadProcessMemory, 100 ms, no injection | t=1.507 service created (`+0x28`=0); reader bug; re-read at t=49: `+0x28`=1, `+0x24`=0, `[0x1227E54]`=0x1, `[0x1227E50]`=0x1. |
| C 13:18:46 | same, from process start, 75 s | t=0: all 0. t=1.508: service created, `+0x28`=0, `+0x24`=0x45F0C48 (pointer-like, not 0). t=2.011: `+0x28`=1, `+0x24`=0, `[0x1227E54]`=0x1, `[0x1227E50]`=0x1, all in the same 100 ms tick. Unchanged until the kill at t=75. |

EA app log (`EADesktopVerbose.log`, run C, UTC): `Launching [...Sims3Launcher.exe]` 05:18:48.577, LSX `ChallengeResponse` 05:18:50.096
(t≈1.5), `QueryEntitlements` request 05:18:50.112, `QueryEntitlementsResponse` 05:18:50.513 (t≈1.9; GraphQL `sdkEntitlements` 401 ms).
Run of 13:02 (other agent, loaded a lot): same shape, request 05:02:02.331, response 05:02:02.733 (399 ms).

## Verdict
- (a) Flags after init = **0x1 (BaseGame only)** in both read runs: the player problem is reproduced on this laptop. [verified]
- (b) The answer arrives ~0.4-0.5 s after the service is created, inside the 3000 ms wait; the flags are written in the same 100 ms tick
  as `+0x28` turns 1. The gate waited for the answer and then mounted only the base game. [verified by 100 ms sampling]
- (c) Skip-wait branch (`jne` 0xA42A38): not taken. Live: `[0x1244FB0]` was 0 until the gate ran (t=1.5). Static: 0xA42A30 has three
  callers (0x596994 = gate, 0x59498A, 0x594D38) and the parent 0x597E0F calls the gate (0x597E1B) before the other two (0x597E28, 0x597E2F). [verified]
- (d) **Not a timing problem; no "always wait" patch**: it would change nothing. The mismatch is in what the game compares against the
  answer (data), so more read-only RE is needed, not a runtime patch.

## Data found for the next step
- Answer: 55 entitlements (filter `ORIGIN_DOWNLOAD`), 26 distinct `ItemId`s. 21 exist as strings in the game image (offer-id string
  table 0x1021F90-0x10227xx, e.g. `OFB-EAST:55107`, `DR:156751300`, `Origin.OFR.50.0000058`); 5 do not: `OFB-EAST:46851`,
  `Origin.OFR.50.0001139` (22 of the 55 entries), `.0002168`, `.0003439`, `.0004172`. [verified]
- The gate's product loop (0x5969C2-0x596A54) compares each pending product's id `[edi+8]` with 12-byte entries at 0x11A7B40 and with the
  list `[0x1227E48]`, then calls 0xA421F0 (entitlement lookup by name) and mounts via 0x596810 only on a hit. Which name each pack expects
  and what 0xA421F0 matches is the open point. [inferred from the disassembly, not traced]
- `0x1227E54` has no code reference in the image; `0x1227E50` has ~140 and read 0x1 live, so the "registry mask at `[0x1227E50]+0x10`"
  attribution is wrong (the value is not a pointer). Which of the two is GetProductFlags is unverified; both say BaseGame only.
- Registry `HKLM\SOFTWARE\WOW6432Node\Sims`: all 19 pack keys exist with only `Install Dir`; the base key also has `DisplayName`,
  `Locale`, `Product GUID`, `ProductID`, `SKU`. Compare with the Steam machine before assuming the game needs the extra values. [verified]

## Next
1. Read-only trace of one pack through the loop (late attach after the menu is safe for Frida, docs/230 dump-image pattern): log the
   id `[edi+8]`, the matched table entry and the 0xA421F0 argument/result; compare with the 26 answered `ItemId`s.
2. Diff the pack registry values with the Steam machine; if the EA app install lacks values the loop reads, that is an EA install bug:
   report it in the forum thread with this timeline as evidence.
Scripts: `tools/probe/ep-gate-probe.js` (Frida, late attach only); the ReadProcessMemory reader lived in the scratchpad (not kept).
