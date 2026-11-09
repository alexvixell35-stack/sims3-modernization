# native/dxvk

Our patches for [DXVK](https://github.com/doitsujin/dxvk) (zlib license) at v3.1.1, the release in the player's Bin
(`d3d9.dll` 265888c31ca7). `build.cmd` fetches the pinned commit, applies `patches\*.patch` and builds `d3d9.dll`:
- x86: TS3W runs it in-process;
- x64: the RTX Remix bridge server loads it as `.trex\d3d9vk_x64.dll` (`native/remix-bridge`).

Build:
- Needs git, VS 2022 C++ build tools (x86 + x64), meson + ninja (`pip install --user meson ninja`) and glslang (the
  KhronosGroup release zip) on PATH.
- Set `DXVK_SRC` to a short path (for example `C:\dxvk-src`): DXVK's nested submodules and build tree pass the
  Windows path limit under a deep checkout.
- A clean build takes about 2 minutes. Output: `out\x86\d3d9.dll` and `out\x64\d3d9.dll`.

DXVK's own releases are built with MinGW gcc, while this build uses MSVC. So A/B runs compare our build with and
without a patch, not our build with the release.

| Patch | What | Why |
|---|---|---|
| 0001 | `DxvkPipelineBindings` binding lists keep 4 entries inline instead of 32 | One `DxvkPipelineBindings` is about 12 KB on x86 (23 `small_vector<DxvkShaderDescriptor, 32>`). DXVK keeps one in every `DxvkShaderPipelineLibrary` (one per shader), `DxvkGraphicsPipeline` and `DxvkComputePipeline`. In TS3 that was 6,464 shader library nodes of 12,808 bytes, 79 MiB of the process heap (#40, al326). A list longer than 4 moves to the heap. |
| 0002 | `DxvkShaderIo` keeps 16 variables inline instead of 32 | A `DxvkShaderIoVar` is 36 bytes on x86, so one `DxvkShaderIo` is 1160 bytes. Every `DxvkIrShader` holds four (inputs and outputs, in its own metadata and in the `DxvkShader` base copy), and every `DxvkGraphicsPipelineFastInstanceKey` five more. In TS3 the 6,570 shader objects are 5896 bytes each (37 MiB, al328). Their inputs hold at most 16 variables and their outputs at most 15, so nothing moves to the heap. |
| 0003 | Diagnostics, test runs only: with `DXVK_TS3_DIAG=1`, logs every 1800 presents the live texture mapping buffers by pool, DXVK's allocated / used memory per Vulkan heap, and the live / created Vulkan pipelines by kind (library, linked, monolithic, compute) | A texture's mapping buffer is host-visible cached Vulkan memory that the driver maps into TS3W (SYSTEMMEM and SCRATCH textures always have one, DEFAULT dynamic ones keep theirs after the first lock). In al328, 16-32 MiB private blocks outside any heap took about 160 MiB; the two of them probed held texture pixels. The pipeline counts are lined up with the NVIDIA driver's own records (#40, al329). Without the variable nothing is logged. |

How the 12,808-byte blocks were matched to `m_shaderLibraries` (`tools/memory/nt-heap.py --dump 12808 --holders 12808`
on al326):
- Each block holds the next block's address at +0, then the key (capacity 4, size 1, one shader pointer) at +8.
- At +0x20 and +0x24 are two pointers, the same in every block (`m_device`, `m_manager`), then the key copy
  (`m_shaders`).
- The only other holder is one 41,104-byte block: a bucket array of about 10,270 pointers.

Checks in TS3 (in-process, `Game\BinBase`, census by `tools/memory/nt-heap.py`):
- 0001 (al328, first sample 2 min after the load): no 12,808-byte blocks. The shader library nodes are 2,568 bytes
  (6,493 blocks, 15.9 MiB instead of 79). The process heap is 164.7 MiB committed, against 219.4 in al326 (the
  release DLL) at the same point.
- 0002: the variable counts come from `nt-heap.py --dump 5896 --raw` on al328 and a scan for the
  `{capacity, size}` pairs of each `small_vector`. The inputs and outputs show the same counts at two places in
  every block: the base class copy and the `DxvkIrShader` copy.
- 0003 (al329, 180 min): the NVIDIA driver keeps about 0.9 text records per complete pipeline created (linked +
  monolithic), including the linked ones DXVK has destroyed: 2,075 records for 1,100 linked (0 live) and 1,183
  monolithic pipelines. Each record takes about 120 KB of the driver's 128 MiB arenas.
