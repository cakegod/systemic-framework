namespace Systemic.Benchmarks;

// Hand-derived expectations for the thinking workload. Row indices are chosen so each case
// exercises one branch: mask off with movement, mask on crossing zero, mask on staying
// non-positive, and the threshold comparison itself.
internal static class ThinkingAgentSelfTest
{
    public static void Run()
    {
        foreach (var safe in new[] { true, false })
        {
            var workload = new ThinkingAgentWorkload(4096, safe);
            workload.Tick();
            workload.Tick();
            workload.Validate(2);

            // Row 2: starts at x=-2046, vx=-1, mask on, never crosses zero -> no write.
            Assert(workload.Sample(2), -2048, 100, "masked-on, non-positive row");
            // Row 2049: starts at x=1, vx=2, mask off -> moves, health untouched.
            Assert(workload.Sample(2049), 5, 100, "masked-off row");
            // Row 2050: starts at x=2, vx=3, mask on -> positive on both ticks.
            Assert(workload.Sample(2050), 8, 98, "masked-on row crossing zero");
            // Row 2052: starts at x=4, vx=-2, mask on -> one write, then falls back to zero.
            Assert(workload.Sample(2052), 0, 99, "masked-on row leaving the threshold");
        }
    }

    private static void Assert((int X, int Health, ulong Mask) sample, int expectedX,
        int expectedHealth, string label)
    {
        if (sample.X != expectedX || sample.Health != expectedHealth)
        {
            throw new InvalidOperationException(
                $"Thinking self-test failed for {label}: x={sample.X} (expected {expectedX}), "
                + $"health={sample.Health} (expected {expectedHealth}).");
        }
    }
}
