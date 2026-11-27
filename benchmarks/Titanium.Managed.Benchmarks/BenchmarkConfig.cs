using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace Titanium.Managed.Benchmarks;

internal sealed class TlsBenchmarkConfig : ManualConfig
{
    public TlsBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(1)
            .WithIterationCount(10)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithLaunchCount(1)
            .WithId("local"));
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(StatisticColumn.Median, StatisticColumn.P95);
        AddExporter(JsonExporter.Full);
        AddLogger(ConsoleLogger.Default);
    }
}
