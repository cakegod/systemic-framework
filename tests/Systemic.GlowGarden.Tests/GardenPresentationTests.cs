using GlowGarden;
using Systemic.Kernel;
using Systemic.Presentation;
using Systemic.Host;
using Xunit;

namespace Systemic.GlowGarden.Tests;

public sealed class GardenPresentationTests
{
    [Fact]
    public void DebugSystemStatsAreCachedAndRefreshedAfterOneSimulationSecond()
    {
        var type = typeof(GardenUi).Assembly.GetType("GlowGarden.GardenTickDiagnostics");
        Assert.NotNull(type);
        var diagnostics = Activator.CreateInstance(type)!;
        var getText = type.GetMethod("GetText")!;
        var calls = 0;
        Func<IReadOnlyList<SystemTickStats>> sample = () =>
        {
            calls++;
            return [new SystemTickStats("garden", 5, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2),
                TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(4), 8, 2)];
        };
        string Read(int tick) => (string)getText.Invoke(diagnostics, [tick, 30, sample])!;
        var first = Read(0);
        Assert.Contains("garden", first);
        Assert.Contains("last 1.00", first);
        Assert.Contains("mean 2.00", first);
        Assert.Contains("p95 3.00", first);
        Assert.Contains("max 4.00", first);
        Assert.Contains("8 B", first);
        Assert.Contains("budget exceeded 2", first);
        Assert.Equal(first, Read(29));
        Assert.Equal(1, calls);
        Read(30);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DiagnosticsReportStreamDropsRejectionsAndDisplayEvictions()
    {
        var stream = new PresentationCueStream(new PresentationCueStreamOptions
        {
            Capacity = 1,
            OverflowPolicy = PresentationCueOverflowPolicy.DropNewest,
        });
        var cue = new PresentationCue(0, 0, "burst", "burst", 0, 0, null, null, null, 1, 0, 0, 0);
        stream.Enqueue(cue);
        stream.Enqueue(cue);
        var text = GardenUi.FormatCueDiagnostics(stream, 3);
        Assert.Contains("dropped 1", text);
        Assert.Contains("rejected 1", text);
        Assert.Contains("display dropped 3", text);

        text = GardenUi.FormatCueDiagnostics(stream, 3, 4);
        Assert.Contains("expired 4", text);
    }

    [Fact]
    public void DebugWaveReportNamesImmutableScheduleWaves()
    {
        var waves = System.Collections.Immutable.ImmutableArray.Create(
            System.Collections.Immutable.ImmutableArray.Create("plants", "workers"),
            System.Collections.Immutable.ImmutableArray.Create("resolve"));

        Assert.Equal("Waves: 0: plants, workers  |  1: resolve", GardenTickDiagnostics.FormatWaves(waves));
    }

    [Fact]
    public void GameExposesItsScheduleAsStableImmutableWaveNames()
    {
        using var host = GardenHost.Create(17);

        Assert.Equal(
            [
                System.Collections.Immutable.ImmutableArray.Create("systemic.schedules"),
                System.Collections.Immutable.ImmutableArray.Create("glow-garden")
            ],
            host.Game.SchedulerWaves);
    }

    [Fact]
    public void GardenViewportFitsTheWholeWorldInsideTheDrawingPanel()
    {
        var viewport = GardenGame.CreateViewport();
        var topLeft = viewport.WorldToScreen(new Point2(0, 0));
        var bottomRight = viewport.WorldToScreen(new Point2(1920, 1280));
        Assert.Equal(20.25f, topLeft.X, 4);
        Assert.Equal(120, topLeft.Y, 4);
        Assert.Equal(825.75f, bottomRight.X, 4);
        Assert.Equal(657, bottomRight.Y, 4);
        Assert.Equal(new Point2(960, 640), viewport.CameraCenter);
        Assert.False(viewport.IsVisible(new Bounds2(-5, 640, 1, 1)));
    }

    [Fact]
    public void TextureCacheFlushesBeforeDisposalAndEvictsTheLeastRecentlyUsed()
    {
        using var cache = new GardenTextureCache<string, FakeTexture>(2);
        var a = new FakeTexture();
        var b = new FakeTexture();
        var c = new FakeTexture();
        var flushes = 0;
        var flushed = false;
        b.Disposing = () => Assert.True(flushed);
        Action flush = () => { flushes++; flushed = true; };
        object Get(string key, FakeTexture candidate) => cache.GetOrCreate(key, () => candidate, flush);

        Assert.Same(a, Get("a", a));
        Assert.Same(b, Get("b", b));
        Assert.Same(a, Get("a", new FakeTexture()));
        Assert.Same(c, Get("c", c));
        Assert.Equal(1, flushes);
        Assert.True(b.Disposed);
        Assert.False(a.Disposed);
        Assert.False(c.Disposed);
        Assert.Equal(2, cache.Count);
        cache.Dispose();
        Assert.True(a.Disposed);
        Assert.True(c.Disposed);
    }

    [Fact]
    public void FailedFlushKeepsTheExistingTextureAlive()
    {
        using var cache = new GardenTextureCache<string, FakeTexture>(1);
        var existing = new FakeTexture();
        cache.GetOrCreate("a", () => existing, () => { });
        Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate("b", () => new FakeTexture(),
            () => throw new InvalidOperationException("flush failed")));
        Assert.False(existing.Disposed);
        Assert.Equal(1, cache.Count);
        Assert.Same(existing, cache.GetOrCreate("a", () => new FakeTexture(), () => { }));
    }

    private sealed class FakeTexture : IDisposable
    {
        public bool Disposed { get; private set; }
        public Action? Disposing { get; set; }
        public void Dispose()
        {
            Disposing?.Invoke();
            Disposed = true;
        }
    }
}
