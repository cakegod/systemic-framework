namespace Systemic.Rendering;

/// <summary>Read side of one swarm. Hosts draw from this and never reach into a simulation.</summary>
public interface ISwarmSource
{
    string Id { get; }

    ReadOnlySpan<SwarmInstance> Instances { get; }

    /// <summary>Bumped after each write. A renderer uploads only when this value changes.</summary>
    int Revision { get; }
}
