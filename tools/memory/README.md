# tools/memory

Read-only memory tools for a running `TS3W.exe` 1.67. They use `VirtualQueryEx` and `ReadProcessMemory` only:
- no lock is taken, nothing is written, and the game is not paused;
- a structure changing during a read can shift a few counts, so run a tool twice before trusting a small difference;
- reads go in windows of at most 4 MiB, so a run beside the game does not need a big buffer.

Run each tool with `python -I` while the game runs. The EA heap and pool tools need the Steam/1.67 exe (addresses
below). Used in the #40 bridge runs (al317-al325) and the lot pool work (docs/541 in PR #313).

| Tool | What it shows | Use it for |
|---|---|---|
| `mem-census.py [--top N] [--state FILE]` | every allocation of the address space by owner: image, mapped file, EA heap core (`ea:main`, `ea:mono-malloc`, `ea:direct` big blocks), Boehm GC section, NT heap segment (`nt-heap:process` = the process heap), thread stack, unlabeled; MiB reserved / committed, the largest free block. `--state` prints what appeared or went away since the last sample. | which owner grows between two points of a run |
| `mem-census-summary.py <log> [--jumps N] [--events harness.log ...]` | summarizes a log of `mem-census.py` samples: first / max / last per owner, the largest free block trend, the biggest jumps | lining growth up with loads, saves and trips |
| `ea-heap-prof.py [--top N] [--out F] [--diff F] [--sizes LABEL]` | walks every chunk of the EA main heap (`[0x11CB864]`) and groups in-use chunks by what they hold: a vtable in the first dword (a C++ object type), a sub-heap or pool core, else a size class. `--sizes` lists one label's exact sizes with an example. | what the EA heap holds, and (with `--diff`) what grew |
| `ea-heap-new.py save` / `diff FILE [--minsize] [--maxsize] [--max]` | which EA main heap chunks of a size range are new since `save`, the chunks that point at them, and how many have no holder in the heap (leak candidates) | who keeps new small blocks alive |
| `ea-heap-free.py` | free space inside each EA heap's cores by bin (fast, small, mid, large, top) | whether the EA heap grows because it is full or because it is fragmented |
| `ea-pool-census.py [--max-read N]` | TS3W's 12 static EA fixed pools (init `0x4E43D0` with `mov ecx, <pool>`): cores, slots, free share, completely free cores | pools that hold free cores |
| `lot-pool-snap.py [pid]` | the 272-byte lot message pool `0x11EB590`: cores, slots, live slots, completely free cores (what `[LotPool] Trim` in PR #313 frees) | before and after a world load or a trim |
| `nt-heap.py [--state FILE] [--top N] [--pid PID] [--dump STRIDE] [--holders STRIDE]`, `--self-check` | the process heap (PEB32 `ProcessHeap`): segments, busy and free backend blocks by size, LFH buckets (block size, busy count from the bitmap, where the busy blocks' first dword points), big blocks. `--state` diffs busy blocks (backend and LFH) between samples, by size and by what their first dword points into. `--dump` shows a few blocks of one bucket; `--holders` finds who points at them. | growth of `nt-heap:process`: live data or fragmentation, which block size, and whose blocks |
| `mem-unlabeled.py [--top N] [--sample N] [--state FILE]`, `--self-check` | the allocations `mem-census.py` leaves unlabeled, grouped by protection (RW, RWX, write-combined) and by a hint: code, all-zero, or the module most of their dwords point into (dwords of four printable bytes are text and are skipped). The biggest allocations with their committed regions. `--state` prints the change per hint since the last sample. | what the unlabeled hundreds of MiB are (driver arenas, Vulkan chunks, JIT code) and which of them grows |
| `mem-strings.py ADDR [MINLEN] [MAXMB]`, `--self-check` | the most common ASCII strings in one allocation, and the share of its bytes that are text | naming an unlabeled allocation by its content |
| `mono-census.py [--top N] [--state FILE]`, `--self-check` | the Mono managed heap by class: walks the Boehm sections and block headers (docs/198), names each object by its vtable's `MonoClass` (name +0x34, namespace +0x38, outer class +0x28), and counts objects whose GC mark bit (block header +0x1C) is set, live at the last collection. `--state` prints the classes that grew most since the last sample. | which managed classes keep growing |
| `ea-heap-leaks.py FILE [--max N] [--maxsize BYTES]`, `--self-check` | EA main heap leak scan in passes. The first pass saves the in-use chunks. Later passes take the chunks that are new since the baseline and alive a whole interval, and search all committed writable memory for a pointer to a sample of them (the user address up to +0x1C). A chunk with no pointer is a candidate, and a candidate with still no pointer at the next pass is confirmed. Prints counts by label (vtable or size) and the first bytes of the confirmed ones. | whether the EA heap leaks (lost blocks) or only holds (blocks still referenced) |

New tools, checked on #40 runs al328-al330:
- `mono-census.py`: the mark bits are at header +0x1C, not +0x18. With them about 150-170 of 200-230 MiB is live, and
  the bits match each block's slot size.
- `mem-unlabeled.py`: the two biggest unlabeled allocations (128 MiB reserved each) belong to the NVIDIA driver. Their
  header points into `nvoglv32.dll`, and `mem-strings.py` shows YAML-like records with hashed keys. Each key repeats about
  4,200 times, maybe one record per compiled pipeline; al329 checks this against DXVK's pipeline counts. Before the text filter, that text made them look like `amdvlk32.dll` memory.
- `ea-heap-leaks.py`: in 15 minutes of al328, 33 chunks (2.3 KB) were confirmed with no pointer. That is far below
  the heap's growth.

`nt-heap.py` details:
- It needs the x86 heap layout of Windows 10/11 and checks both heap signatures before it reads anything.
- Its free total should match the heap's own `TotalFreeSize`, which it prints.
- Inside an LFH user block, the header's offsets are encoded (Windows 8+). So the block size comes from the user
  block's size and its block count. That is exact for small blocks but too big for large ones (13512 for a real 12808),
  so it is checked against the blocks' own 8-byte headers: the size is the largest one at which every block header
  has bit 0x80 in its last byte.
- `--holders` reads the game's other private and image memory too (about 1.7 GB in 4 MiB windows, a minute on TS3W).
  Don't run it while memory is low.
- Example (al325, bridge, 60 min): the heap grew from LFH fragmentation in the 32-byte bucket, while its live blocks
  did not grow. 97% of its live blocks were the bridge diagnostics' table nodes (issue #40, al325).
- Example (al326, in-process DXVK 3.1.1): the 12808-byte bucket holds 6,464 blocks (79 MiB), about one per game
  shader. `--holders` shows them chained through their first dword with a bucket array in one 41 KB block, which is a
  hash map's nodes. The 5896-byte bucket (6,594 blocks, 37 MiB) starts with a `d3d9.dll` vtable.
