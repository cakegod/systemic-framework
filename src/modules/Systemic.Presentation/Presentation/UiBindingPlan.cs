using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Systemic.Presentation;

internal enum UiBindingKind
{
    Unsupported,
    WorldTick,
    World,
    Selection,
    Event,
    Choice,
}

internal readonly record struct UiWorldBindingCandidate(string EntityPath, string MemberPath, string? Tag);

internal sealed record UiBindingDescriptor(
    string Binding,
    UiBindingKind Kind,
    string? ValuePath = null,
    string? EntityPath = null,
    ImmutableArray<UiWorldBindingCandidate> WorldCandidates = default,
    bool HasGrammarShape = true)
{
    public bool HasChoicePrefix => Binding.StartsWith("choice.", StringComparison.Ordinal);
}

/// <summary>Parses the shared authored UI binding grammar once for compiler checks and runtime use.</summary>
internal static class UiBindingParser
{
    public static UiBindingDescriptor Parse(string binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (binding == "world.tick")
        {
            return new UiBindingDescriptor(binding, UiBindingKind.WorldTick);
        }

        if (binding == "world.")
        {
            return new UiBindingDescriptor(binding, UiBindingKind.World,
                WorldCandidates: [], HasGrammarShape: false);
        }

        if (binding == "selection.")
        {
            return new UiBindingDescriptor(binding, UiBindingKind.Selection, string.Empty, HasGrammarShape: false);
        }

        if (binding == "event.")
        {
            return new UiBindingDescriptor(binding, UiBindingKind.Event, string.Empty, HasGrammarShape: false);
        }

        if (binding == "choice.")
        {
            return new UiBindingDescriptor(binding, UiBindingKind.Choice, HasGrammarShape: false);
        }

        if (TryParsePrefixedPath(binding, "world.", out var worldPath))
        {
            var candidates = ImmutableArray.CreateBuilder<UiWorldBindingCandidate>();
            for (var separator = worldPath.LastIndexOf('.'); separator > 0;
                 separator = worldPath.LastIndexOf('.', separator - 1))
            {
                var entityPath = worldPath[..separator];
                var memberPath = worldPath[(separator + 1)..];
                var tag = memberPath.StartsWith("tag.", StringComparison.Ordinal)
                    ? memberPath["tag.".Length..]
                    : null;
                candidates.Add(new UiWorldBindingCandidate(entityPath, memberPath, tag));
            }

            return new UiBindingDescriptor(binding, UiBindingKind.World, WorldCandidates: candidates.ToImmutable());
        }

        if (TryParsePrefixedPath(binding, "selection.", out var selectionPath))
        {
            return new UiBindingDescriptor(binding, UiBindingKind.Selection, selectionPath);
        }

        if (TryParsePrefixedPath(binding, "event.", out var eventPath))
        {
            return new UiBindingDescriptor(binding, UiBindingKind.Event, eventPath);
        }

        if (TryParsePrefixedPath(binding, "choice.", out var choicePath))
        {
            var separator = choicePath.LastIndexOf('.');
            return separator > 0
                ? new UiBindingDescriptor(binding, UiBindingKind.Choice,
                    choicePath[(separator + 1)..], choicePath[..separator])
                : new UiBindingDescriptor(binding, UiBindingKind.Choice);
        }

        return new UiBindingDescriptor(binding, UiBindingKind.Unsupported, HasGrammarShape: false);
    }

    private static bool TryParsePrefixedPath(string binding, string prefix, out string path)
    {
        if (binding.StartsWith(prefix, StringComparison.Ordinal) && binding.Length > prefix.Length)
        {
            path = binding[prefix.Length..];
            return true;
        }

        path = string.Empty;
        return false;
    }
}

/// <summary>
/// Holds parsed binding syntax for an immutable layout. The weak identity cache avoids retaining layouts
/// that callers no longer use and does not merge distinct record instances by value equality.
/// </summary>
internal sealed class UiBindingPlan
{
    private static readonly ConditionalWeakTable<UiLayout, UiBindingPlan> s_cache = [];

    private UiBindingPlan(UiLayout layout)
    {
        Root = BuildNode(layout.Root);
    }

    public UiBindingNodePlan Root { get; }

    public static UiBindingPlan For(UiLayout layout) => s_cache.GetValue(layout, static value => new UiBindingPlan(value));

    private static UiBindingNodePlan BuildNode(UiNode node)
    {
        var binding = node.Bind is { } value ? UiBindingParser.Parse(value) : null;
        var children = ImmutableArray.CreateBuilder<UiBindingNodePlan>(node.Children.Length);
        foreach (var child in node.Children)
        {
            children.Add(BuildNode(child));
        }

        return new UiBindingNodePlan(binding, children.MoveToImmutable());
    }
}

internal sealed record UiBindingNodePlan(UiBindingDescriptor? Binding, ImmutableArray<UiBindingNodePlan> Children);
