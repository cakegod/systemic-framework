using Systemic.Content;
using Systemic.Preview;

namespace Systemic.Tests.Preview;

public sealed class PresentationPreviewTests
{
    [Fact]
    public static void Preview_exposes_timeline_controls_and_event_bindings()
    {
        var pack = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));

        var html = ContentPreviewPage.Render(pack);

        Assert.Contains("Presentation timelines", html, StringComparison.Ordinal);
        Assert.Contains("quarry.impact", html, StringComparison.Ordinal);
        Assert.Contains("incremental.production", html, StringComparison.Ordinal);
        Assert.Contains("timeline-seek", html, StringComparison.Ordinal);
    }
}
