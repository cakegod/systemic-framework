using System.Numerics;
using System.Runtime.InteropServices;
using Systemic.Rendering;

namespace Systemic.Rendering.Tests;

public sealed class SwarmInstanceTests
{
    [Fact]
    public void Instance_stays_small_and_layout_is_fixed()
    {
        Assert.Equal(20, Marshal.SizeOf<SwarmInstance>());
    }

    [Fact]
    public void Packed_colour_round_trips()
    {
        var packed = SwarmColor.Pack(12, 34, 56, 78);

        Assert.Equal((12, 34, 56, 78), SwarmColor.Unpack(packed));
    }

    [Fact]
    public void Default_alpha_is_opaque()
    {
        Assert.Equal(255, SwarmColor.Unpack(SwarmColor.Pack(1, 2, 3)).Alpha);
    }
}

public sealed class SwarmSourceTests
{
    [Fact]
    public void Write_grows_past_capacity_and_exposes_written_instances()
    {
        var source = new SwarmSource("boids", capacity: 2);

        var span = source.BeginWrite(5).Span;
        for (var index = 0; index < span.Length; index++)
        {
            span[index] = new SwarmInstance(new Vector2(index, index * 2), 1.0f, 0.0f, 0xFFFFFFFF);
        }

        source.EndWrite();

        Assert.Equal(5, source.Instances.Length);
        Assert.Equal(new Vector2(4, 8), source.Instances[4].Position);
    }

    [Fact]
    public void Revision_bumps_once_per_completed_write()
    {
        var source = new SwarmSource("boids");

        source.BeginWrite(1);
        Assert.Equal(0, source.Revision);

        source.EndWrite();
        Assert.Equal(1, source.Revision);

        source.BeginWrite(0);
        source.EndWrite();
        Assert.Equal(2, source.Revision);
    }

    [Fact]
    public void Open_write_keeps_the_previous_snapshot_visible()
    {
        var source = new SwarmSource("boids", capacity: 4);
        var span = source.BeginWrite(1).Span;
        span[0] = new SwarmInstance(new Vector2(1, 1), 1.0f, 0.0f, 0);
        source.EndWrite();

        source.BeginWrite(4).Span[0] = new SwarmInstance(new Vector2(9, 9), 1.0f, 0.0f, 0);

        Assert.Equal(1, source.Instances.Length);
        Assert.Equal(new Vector2(1, 1), source.Instances[0].Position);
    }

    [Fact]
    public void Published_instances_survive_the_next_write()
    {
        var source = new SwarmSource("boids", capacity: 2);
        var first = source.BeginWrite(2).Span;
        first[0] = new SwarmInstance(new Vector2(1, 1), 1.0f, 0.0f, 0);
        first[1] = new SwarmInstance(new Vector2(2, 2), 1.0f, 0.0f, 0);
        source.EndWrite();
        var published = source.Instances;

        var second = source.BeginWrite(2).Span;
        second[0] = new SwarmInstance(new Vector2(8, 8), 1.0f, 0.0f, 0);
        second[1] = new SwarmInstance(new Vector2(9, 9), 1.0f, 0.0f, 0);
        source.EndWrite();

        Assert.Equal(new Vector2(1, 1), published[0].Position);
        Assert.Equal(new Vector2(2, 2), published[1].Position);
        Assert.Equal(new Vector2(8, 8), source.Instances[0].Position);
    }

    [Fact]
    public void Shrinking_write_hides_previous_instances()
    {
        var source = new SwarmSource("boids");
        source.BeginWrite(3);
        source.EndWrite();

        source.BeginWrite(1);
        source.EndWrite();

        Assert.Equal(1, source.Instances.Length);
    }

    [Fact]
    public void Written_values_become_visible_after_the_write_ends()
    {
        var source = new SwarmSource("boids");
        var span = source.BeginWrite(1).Span;
        span[0] = new SwarmInstance(new Vector2(3, 4), 2.0f, 0.5f, SwarmColor.Pack(9, 8, 7));

        source.EndWrite();

        Assert.Equal(new Vector2(3, 4), source.Instances[0].Position);
        Assert.Equal(2.0f, source.Instances[0].Size);
        Assert.Equal(SwarmColor.Pack(9, 8, 7), source.Instances[0].Rgba);
    }
}

public sealed class SwarmSceneTests
{
    [Fact]
    public void Scene_totals_instances_of_every_source()
    {
        var first = new SwarmSource("first");
        var second = new SwarmSource("second");
        var scene = CreateScene(first, second);
        first.BeginWrite(3);
        second.BeginWrite(2);

        first.EndWrite();

        Assert.Equal(3, scene.InstanceCount);

        second.EndWrite();

        Assert.Equal(5, scene.InstanceCount);
    }

    [Fact]
    public void Removed_source_stops_contributing()
    {
        var source = new SwarmSource("boids");
        var scene = CreateScene(source);
        var span = source.BeginWrite(4).Span;
        span.Fill(new SwarmInstance(Vector2.Zero, 1.0f, 0.0f, 0));
        source.EndWrite();

        Assert.True(scene.Remove(source));

        Assert.Empty(scene.Sources);
        Assert.Equal(0, scene.InstanceCount);
    }

    [Fact]
    public void Duplicate_source_id_is_rejected()
    {
        var scene = CreateScene(new SwarmSource("boids"));

        Assert.Throws<ArgumentException>(() => scene.Add(new SwarmSource("boids")));
    }

    private static SwarmScene CreateScene(params ISwarmSource[] sources)
    {
        var scene = new SwarmScene();
        foreach (var source in sources)
        {
            scene.Add(source);
        }

        return scene;
    }
}
