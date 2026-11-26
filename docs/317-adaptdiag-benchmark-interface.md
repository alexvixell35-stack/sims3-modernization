# 317. Adaptive diag kit: interface for the benchmark lane

2026-11-21, branch `feature/adaptive-diag-kit-1121` (owner of the kit installer, the state file and the report format).
Design and rules engine: docs/316. This page is the contract a "benchmark mode" (branch `feature/kit-benchmark-1121`)
builds against; change it only on the adaptive-diag-kit branch.

## 1. State file

`Game\Bin\ts3native.adaptdiag-state.ini` (next to `ts3native.asi`), plain ASCII INI, read with `GetPrivateProfile*` by
ts3native at game start (only when `[AdaptDiag] Enabled=1`, which is set in the kit's diag ini only; the release ini has
no such section, so a release install never reads it).

| Section | Owner | Keys |
|---|---|---|
| `[Session]` | kit / benchmark lane writes, ts3native only reads | `Benchmark=0\|1`, `BenchmarkName=<ascii, <= 40 chars>`, `BenchmarkRun=<ascii id, <= 40>`, `Arm=<ascii, <= 16; empty = no A/B>` |
| `[Live]` | ts3native writes | `Session=<MMDDhhmm>`, `Clean=0\|1` (1 = the last session reached a clean exit), `ModSet=<16 hex>`, `Config=<16 hex>`, `Arm=`, `Benchmark=` (copies of the session that wrote them) |
| `[Escalate]` | ts3native writes (rule engine), consumed at the next start | `<Section>.<Key>=<value>` one per line + `Rule=<id list>`; allow-listed keys only (docs/316 §4) |
| `[Applied]` | ts3native writes | what the last start applied from `[Escalate]` |

Rules for the benchmark lane:

- Set `[Session] Benchmark=1` (and name / run id) **before** starting the game, set it back to `0` after the run. The kit's
  install, uninstall and report steps reset it to 0 as well, so a crashed benchmark cannot label later normal play.
- PowerShell helper in `tools/player/ts3-kit/tools/TS3Kit.ps1`: `SetAdSession $binDir 'Benchmark' '1'` (writes one key of
  `[Session]`, creates the file when missing, ASCII). Use it rather than writing the file by hand.
- With `Benchmark=1` (or a non-empty `Arm=`) the rule engine never turns a detector on at run time: it logs what it would
  have done (`adaptdiag: next session turns on <id> ...`) and writes it to `[Escalate]` for the next session, so every benchmark / A/B arm runs
  with identical diagnostics and its timing is not disturbed.

## 2. Files ts3native writes (Game\Bin, next to ts3native.log)

- `adaptdiag.csv`: one row per real minute. Columns (fixed order, header line first):
  `minute,activity,noise,bench,arm,simfps,iter250,iter1000,longestms,freemb,commitmb,scripterr,othercpu,perfpct,tempc,av,drill`
  `activity` = dominant tag of the minute: `live|cas|buildbuy|load|travel|save|paused`; `noise` = `A|B|C` (C = discounted);
  `bench` = 0/1; `drill` = `+`-joined active drill-down ids.
- `adaptdiag-report.txt`: per-session report, rewritten every 5 minutes and at world quit / exit. Sections in this order:
  `## session`, `## install check`, `## activity summary`, `## noise`, `## benchmark` (only when `Benchmark=1`),
  `## drill-downs` (issue -> evidence -> detector -> finding -> next step), `## known issues`, `## a/b validity`.
  The `## benchmark` section lists name, run id, minutes, clean minutes and per-activity sim fps p50 / hitches per clean
  minute / longest pause, computed from the rows with `bench=1`.
- `adaptdiag-sessions.csv`: one appended row per session: `session,start,arm,bench,benchname,modset,config,clean`.

## 3. Files the benchmark lane may add

- `Game\Bin\adaptdiag-bench.txt` (any ASCII text, <= 5 MB): the benchmark's own result. The kit's report step copies it
  into the diag zip (masked, as every text file). ts3native's own `## benchmark` section does not read it.
- Further files go under `Game\Bin\adaptdiag-bench\`: the newest 30 with a text extension (`.ini .toml .conf .cfg .log
  .jsonl .txt .xml .csv`, <= 5 MB each) are zipped, masked; nothing else there is picked up.

## 4. Diag zip layout (owned here)

`TS3Kit.bat` -> 6 (Make report) or `TS3Kit.bat -Mode Report [-GameDir <game>] [-Yes]` lists the files and their size,
asks, then builds the same masked `TS3Diag-*.zip` as `-Mode Diag` and prints the zip size; afterwards `[Session]
Benchmark` is 0 again. Inside the zip, per game install `n`, `files\game<n>-bin\` holds `adaptdiag-report.txt`,
`adaptdiag.csv`, `adaptdiag-sessions.csv`, `ts3native.adaptdiag-state.ini`, `adaptdiag-bench.txt` and
`adaptdiag-bench\*`; the newest crash reports are under `files\user<n>\crash\`. Everything else stays where `-Mode Diag`
puts it (kit 1121.a1; `test-kit.ps1` checks the report, csv and bench paths and the masking).

## 5. Stability

Column order and section names above are fixed for kit 1121.a1 and later; new columns are appended at the end only.
