using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

namespace Groundwork.Benchmarks;

internal static class BenchmarkSuite
{
    internal static int Run(string[] args)
    {
        _ = BenchmarkSwitcher.FromAssembly(typeof(BenchmarkSuite).Assembly)
            .Run(args, DefaultConfig.Instance);
        return 0;
    }
}
