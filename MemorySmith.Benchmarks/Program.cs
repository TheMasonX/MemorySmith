using BenchmarkDotNet.Running;
using MemorySmith.Benchmarks;

if (args.Any(arg => string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase)))
{
    await SearchBenchmarks.RunSmokeAsync();
    await CodeSearchBenchmarks.RunSmokeAsync();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(SearchBenchmarks).Assembly).Run(args);