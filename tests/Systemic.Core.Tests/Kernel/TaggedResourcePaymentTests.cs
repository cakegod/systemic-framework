using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class TaggedResourcePaymentTests
{
    private static readonly object?[] s_listCandidateArguments =
    [
        new[] { "payment.ore" },
        new[] { "payment.use", EntityCapabilities.BeSpent }
    ];

    private static readonly object?[] s_directCandidateArguments =
    ["payment.ore", "payment.use", EntityCapabilities.BeSpent];

    [Fact]
    public void Payment_plan_uses_ascending_restored_entity_ids()
    {
        var world = new World(seed: 1, valueScale: 1);
        var laterId = AddResource(world, id: 41, amount: 2, "payment.ore");
        var earlierId = AddResource(world, id: 7, amount: 1, "payment.ore");
        MakeSpendable(world, laterId);
        MakeSpendable(world, earlierId);

        var result = new TaggedResourcePayment("payment.ore", "payment.use", 2.5).TryPay(world);

        Assert.Equal(
            [new ResourcePayment(7, 1), new ResourcePayment(41, 1.5)],
            result);
        Assert.Equal(0, earlierId.Amount);
        Assert.Equal(0.5, laterId.Amount);
    }

    [Fact]
    public void Payment_eligibility_includes_grants_and_requires_both_capabilities()
    {
        var world = new World(seed: 2, valueScale: 1);
        var granted = AddResource(world, id: 9, amount: 2);
        var missingSpendCapability = AddResource(world, id: 4, amount: 10, "payment.ore");
        missingSpendCapability.AddCapability("payment.use");
        var missingRequestedCapability = AddResource(world, id: 5, amount: 10, "payment.ore");
        var nonResource = world.AddRestored(
            new DataEntity(["payment.ore"], ["payment.use", EntityCapabilities.BeSpent]), 6);

        // Build the index before grants are applied so the test covers incremental grant membership.
        Assert.Equal(missingSpendCapability.Id, world.FirstTagged("payment.ore", "payment.use"));
        world.Grant("test.spend", missingRequestedCapability, GrantKind.Capability, EntityCapabilities.BeSpent);
        world.Grant("test.tag", granted, GrantKind.Tag, "payment.ore");
        world.Grant("test.use", granted, GrantKind.Capability, "payment.use");
        world.Grant("test.spend", granted, GrantKind.Capability, EntityCapabilities.BeSpent);

        Assert.True(world.HasTag(granted, "payment.ore"));
        Assert.True(world.HasCapability(granted, "payment.use"));
        Assert.True(world.HasCapability(granted, EntityCapabilities.BeSpent));
        Assert.True(world.HasCapability(nonResource, EntityCapabilities.BeSpent));

        var result = new TaggedResourcePayment("payment.ore", "payment.use", 2).TryPay(world);

        Assert.Equal([new ResourcePayment(granted.Id, 2)], result);
        Assert.Equal(0, granted.Amount);
        Assert.Equal(10, missingSpendCapability.Amount);
        Assert.Equal(10, missingRequestedCapability.Amount);
    }

    [Fact]
    public void Insufficient_payment_does_not_spend_any_candidate()
    {
        var world = new World(seed: 3, valueScale: 1);
        var first = AddResource(world, id: 2, amount: 0.4, "payment.ore");
        var second = AddResource(world, id: 1, amount: 0.6, "payment.ore");
        MakeSpendable(world, first);
        MakeSpendable(world, second);

        var result = new TaggedResourcePayment("payment.ore", "payment.use", 1.5).TryPay(world);

        Assert.Null(result);
        Assert.Equal(0.4, first.Amount);
        Assert.Equal(0.6, second.Amount);

        var withinTolerance = new TaggedResourcePayment("payment.ore", "payment.use", 1 + 0.5e-9)
            .TryPay(world);

        Assert.Equal([new ResourcePayment(second.Id, 0.6), new ResourcePayment(first.Id, 0.4)], withinTolerance);
        Assert.Equal(0, first.Amount);
        Assert.Equal(0, second.Amount);
    }

    [Fact]
    public void Payment_spends_are_reverted_by_world_rollback()
    {
        var world = new World(seed: 4, valueScale: 1);
        var first = AddResource(world, id: 1, amount: 1.25, "payment.ore");
        var second = AddResource(world, id: 2, amount: 2, "payment.ore");
        MakeSpendable(world, first);
        MakeSpendable(world, second);

        using (var transaction = world.BeginTransaction())
        {
            var result = new TaggedResourcePayment("payment.ore", "payment.use", 2).TryPay(world);
            Assert.Equal([new ResourcePayment(first.Id, 1.25), new ResourcePayment(second.Id, 0.75)], result);
            transaction.Rollback();
        }

        Assert.Equal(1.25, first.Amount);
        Assert.Equal(2, second.Amount);
    }

    [Fact]
    public void Zero_and_invalid_payment_amounts_preserve_current_results()
    {
        var world = new World(seed: 5, valueScale: 1);
        var resource = AddResource(world, id: 1, amount: 3, "payment.ore");
        MakeSpendable(world, resource);

        var zero = new TaggedResourcePayment("payment.ore", "payment.use", 0).TryPay(world);
        var negative = new TaggedResourcePayment("payment.ore", "payment.use", -1).TryPay(world);
        var notANumber = new TaggedResourcePayment("payment.ore", "payment.use", double.NaN).TryPay(world);
        var infinity = new TaggedResourcePayment("payment.ore", "payment.use", double.PositiveInfinity).TryPay(world);

        Assert.NotNull(zero);
        Assert.Empty(zero);
        Assert.Null(negative);
        Assert.Null(notANumber);
        Assert.Null(infinity);
        Assert.Equal(3, resource.Amount);
    }

    [Fact]
    public void Three_string_candidate_overload_matches_list_overload_for_granted_membership()
    {
        var world = new World(seed: 6, valueScale: 1);
        var granted = AddResource(world, id: 9, amount: 0);
        var staticMember = AddResource(world, id: 2, amount: 0, "payment.ore");
        staticMember.AddCapability("payment.use");
        staticMember.AddCapability(EntityCapabilities.BeSpent);

        Assert.Equal(staticMember.Id, world.FirstTagged("payment.ore", "payment.use"));
        world.Grant("test.tag", granted, GrantKind.Tag, "payment.ore");
        world.Grant("test.use", granted, GrantKind.Capability, "payment.use");
        world.Grant("test.spend", granted, GrantKind.Capability, EntityCapabilities.BeSpent);

        var listMethod = typeof(World).GetMethod("AllTaggedCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(IReadOnlyList<string>), typeof(IReadOnlyList<string>)],
            modifiers: null);
        var directMethod = typeof(World).GetMethod("AllTaggedCandidates",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(string), typeof(string)],
            modifiers: null);
        Assert.NotNull(listMethod);
        Assert.NotNull(directMethod);

        var expected = (ReadOnlyMemory<int>)listMethod.Invoke(world,
            s_listCandidateArguments)!;
        var actual = (ReadOnlyMemory<int>)directMethod.Invoke(world,
            s_directCandidateArguments)!;

        Assert.Equal([staticMember.Id, granted.Id], expected.ToArray());
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    private static SimpleResourceEntity AddResource(World world, int id, double amount, params string[] tags)
    {
        var resource = world.AddRestored(new SimpleResourceEntity("ore", 100, tags), id);
        Assert.Equal(amount, resource.TryAdd(amount));
        return resource;
    }

    private static void MakeSpendable(World world, ResourceEntity resource)
    {
        world.Grant("test.use", resource, GrantKind.Capability, "payment.use");
        world.Grant("test.spend", resource, GrantKind.Capability, EntityCapabilities.BeSpent);
    }
}
