// 2026-11-20, docs/215 G1/G2: dump VirtualAlloc / VirtualFree / MapFile / UnmapFile events of one process from a WPR trace,
// with module+offset stacks. usage: etlread <file.etl> <process name, e.g. TS3W> [min bytes, default 1 MB]
// Output (one line each): A|F|M|U <ms> tid=<tid> base=0x.. size=0x.. <payload> | <frame> < <frame> ...  (innermost first)
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

var etl = args[0];
var want = args[1];
long min = args.Length > 2 ? long.Parse(args[2]) : 1 << 20;
var etlx = Path.ChangeExtension(etl, ".etlx");
if (!File.Exists(etlx)) TraceLog.CreateFromEventTraceLogFile(etl, etlx);
using var log = new TraceLog(etlx);
var pids = log.Processes.Where(p => p.Name.Equals(want, StringComparison.OrdinalIgnoreCase)).Select(p => p.ProcessID).ToHashSet();
foreach (var p in log.Processes.Where(p => pids.Contains(p.ProcessID)))
    Console.WriteLine($"# process {p.Name} pid={p.ProcessID} start={p.StartTimeRelativeMsec:F0} end={p.EndTimeRelativeMsec:F0} 64bit={p.Is64Bit}");

if (want == "?")  // list event names with payload names, to check the decoder
{
    foreach (var g in log.Events.GroupBy(e => e.ProviderName + "/" + e.EventName)) Console.WriteLine($"{g.Count()} {g.Key} [{string.Join(',', g.First().PayloadNames)}]");
    return;
}
if (args.Length > 3 && args[3] == "mods")  // image list of the process: base, size, load / unload ms, path
{
    foreach (var p in log.Processes.Where(p => pids.Contains(p.ProcessID)))
        foreach (var m in p.LoadedModules.OrderByDescending(m => m.ModuleFile.ImageSize))
            Console.WriteLine($"mod 0x{m.ImageBase:X} {m.ModuleFile.ImageSize / 1048576.0:F1}MB load={m.LoadTimeRelativeMSec:F0} unload={m.UnloadTimeRelativeMSec:F0} {m.ModuleFile.FilePath}");
    return;
}
static long Num(object v) => v switch { ulong u => (long)u, long l => l, int i => i, uint u => u, _ => 0 };
int n = 0;
foreach (var ev in log.Events)
{
    var name = ev.EventName ?? "";
    char kind = name == "VirtualMem/Alloc" ? 'A' : name == "VirtualMem/Free" ? 'F' : name == "FileIO/MapFile" ? 'M' : name == "FileIO/UnmapFile" ? 'U' : ' ';
    if (kind == ' ') continue;
    // VirtualAlloc/Free carry the target process in the payload (the event can come from another process' thread)
    int pid = ev.ProcessID;
    var names = ev.PayloadNames;
    long baseAddr = 0, size = 0;
    var extra = new List<string>();
    for (int i = 0; i < names.Length; i++)
    {
        var k = names[i];
        var v = ev.PayloadValue(i);
        if (k is "BaseAddress" or "BaseAddr" or "ViewBase") baseAddr = Num(v);
        else if (k is "RegionSize" or "Length" or "ViewSize") size = Num(v);
        else if (k == "ProcessID" || k == "ProcessId") { if (Num(v) != 0) pid = (int)Num(v); }
        else extra.Add($"{k}={v}");
    }
    if (!pids.Contains(pid)) continue;
    if (kind is 'A' or 'M' && size < min) continue;
    if (kind is 'F' or 'U' && size != 0 && size < min) continue;
    var frames = new List<string>();
    for (var cs = ev.CallStack(); cs != null && frames.Count < 120; cs = cs.Caller)
    {
        var ca = cs.CodeAddress;
        var mf = ca.ModuleFile;
        frames.Add(mf != null ? $"{mf.Name}+0x{ca.Address - mf.ImageBase:X}" : $"0x{ca.Address:X}");
    }
    Console.WriteLine($"{kind} {ev.TimeStampRelativeMSec:F0} tid={ev.ThreadID} base=0x{baseAddr:X} size=0x{size:X} {string.Join(' ', extra)} | {string.Join(" < ", frames)}");
    n++;
}
Console.WriteLine($"# events {n}");
