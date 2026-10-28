using BenchmarkDotNet.Running;
using Systemic.Benchmarks;

if (args.Length > 0 && string.Equals(args[0], "--gate", StringComparison.Ordinal))
{
    Environment.ExitCode = PerformanceGate.Run(args[1..]);
}
else if (args.Length > 0 && string.Equals(args[0], "--spatial", StringComparison.Ordinal))
{
    Environment.ExitCode = SpatialGridGate.Run(args[1..]);
}
else if (args.Length > 0 && string.Equals(args[0], "--million-agents", StringComparison.Ordinal))
{
    Environment.ExitCode = MillionAgentBenchmark.Run(args[1..]);
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
