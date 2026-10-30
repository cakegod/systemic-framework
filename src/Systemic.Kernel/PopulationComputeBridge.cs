using System.Buffers;

namespace Systemic.Kernel;

/// <summary>Backend-neutral, fixed-point movement input and output for a homogeneous population.</summary>
public readonly record struct PopulationComputeAgent(FixedVector2 Position, FixedVector2 Velocity);

/// <summary>Aggregate positions in exact Q32.32 raw units, without floating-point reduction.</summary>
public readonly record struct PopulationComputeResult(int Count, Int128 SumXRaw, Int128 SumYRaw);

/// <summary>
/// A bridge for CPU or external compute backends. Implementations consume the entire input
/// before publishing output and must leave the destination unchanged on failure or cancellation.
/// The caller must keep input and output alive and unmodified until the returned operation completes.
/// </summary>
public interface IPopulationComputeBackend
{
    ValueTask<PopulationComputeResult> IntegrateAsync(ReadOnlyMemory<PopulationComputeAgent> input,
        Memory<PopulationComputeAgent> output, Fixed64 elapsed, CancellationToken cancellationToken = default);
}

/// <summary>Portable CPU reference semantics for implementing and checking other compute backends.</summary>
public sealed class CpuPopulationComputeBackend : IPopulationComputeBackend
{
    public ValueTask<PopulationComputeResult> IntegrateAsync(ReadOnlyMemory<PopulationComputeAgent> input,
        Memory<PopulationComputeAgent> output, Fixed64 elapsed, CancellationToken cancellationToken = default)
    {
        if (output.Length < input.Length)
        {
            throw new ArgumentException("Output is smaller than input.", nameof(output));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var scratch = ArrayPool<PopulationComputeAgent>.Shared.Rent(input.Length);
        try
        {
            var source = input.Span;
            Int128 sumX = 0, sumY = 0;
            for (var index = 0; index < source.Length; index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var agent = source[index];
                var position = agent.Position + (agent.Velocity * elapsed);
                scratch[index] = agent with { Position = position };
                sumX += position.X.RawValue;
                sumY += position.Y.RawValue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            scratch.AsSpan(0, source.Length).CopyTo(output.Span);
            return ValueTask.FromResult(new PopulationComputeResult(source.Length, sumX, sumY));
        }
        finally { ArrayPool<PopulationComputeAgent>.Shared.Return(scratch); }
    }
}

/// <summary>Stable row selection for systems that deliberately update a fraction each tick.</summary>
public static class FixedUpdateLod
{
    public static bool ShouldUpdate(ulong stableId, long tick, int period)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(period);
        return stableId % (ulong)period == (ulong)tick % (ulong)period;
    }
}
