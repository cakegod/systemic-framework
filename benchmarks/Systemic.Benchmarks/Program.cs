using BenchmarkDotNet.Running;
using Systemic.Benchmarks;

if (args.Length > 0 && string.Equals(args[0], "--gate", StringComparison.Ordinal))
{
    Environment.ExitCode = PerformanceGate.Run(args[1..]);
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
