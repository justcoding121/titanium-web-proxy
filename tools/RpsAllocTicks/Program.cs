using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// Aggregates GCAllocationTick events (Microsoft-Windows-DotNETRuntime GC keyword, verbose level, i.e. the
// dotnet-trace "gc-verbose" profile) from a .nettrace file into per-type allocated bytes.
// AllocationAmount64 is the number of bytes allocated since the previous tick of that kind (about 100 KiB),
// so summing it per sampled type estimates the allocation volume by type (statistical, not exact).
//
// Usage: RpsAllocTicks <file.nettrace> [--pid N] [--top N]
// Output (TSV on stdout): total line first, then "type<TAB>bytes<TAB>ticks<TAB>percent".

var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
if (path == null || !File.Exists(path))
{
    Console.Error.WriteLine("usage: RpsAllocTicks <file.nettrace> [--pid N] [--top N]");
    return 2;
}

int? pid = null;
var top = 40;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--pid" && i + 1 < args.Length) pid = int.Parse(args[++i]);
    else if (args[i] == "--top" && i + 1 < args.Length) top = int.Parse(args[++i]);
}

var bytes = new Dictionary<string, long>();
var ticks = new Dictionary<string, long>();
long total = 0;
double firstMs = double.NaN, lastMs = double.NaN;

using (var source = new EventPipeEventSource(path))
{
    source.Clr.GCAllocationTick += e =>
    {
        if (pid is { } p && e.ProcessID != p) return;
        var name = string.IsNullOrEmpty(e.TypeName) ? "(unknown)" : e.TypeName;
        var amount = e.AllocationAmount64 != 0 ? e.AllocationAmount64 : e.AllocationAmount;
        bytes[name] = bytes.GetValueOrDefault(name) + amount;
        ticks[name] = ticks.GetValueOrDefault(name) + 1;
        total += amount;
        if (double.IsNaN(firstMs)) firstMs = e.TimeStampRelativeMSec;
        lastMs = e.TimeStampRelativeMSec;
    };
    source.Process();
}

var spanSec = double.IsNaN(firstMs) ? 0 : Math.Max(0.001, (lastMs - firstMs) / 1000.0);
Console.WriteLine($"TOTAL\t{total}\t{ticks.Values.Sum()}\t100.00\tspan_s={spanSec:F2}");
foreach (var kv in bytes.OrderByDescending(k => k.Value).Take(top))
{
    var pct = total == 0 ? 0 : 100.0 * kv.Value / total;
    Console.WriteLine($"{kv.Key}\t{kv.Value}\t{ticks[kv.Key]}\t{pct:F2}");
}

return 0;
