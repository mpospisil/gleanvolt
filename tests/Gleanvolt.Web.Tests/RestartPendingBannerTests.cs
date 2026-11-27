using Bunit;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The one "saved, not running until a restart" banner every restart-to-apply tab of Settings shows
/// (2026-11-27), where each form used to carry its own copy.
/// </summary>
public class RestartPendingBannerTests : BunitContext
{
    [Fact]
    public void Says_nothing_with_nothing_pending()
    {
        var banner = Render<RestartPendingBanner>(parameters => parameters.Add(b => b.Count, 0).Add(b => b.Id, "x"));

        Assert.Empty(banner.Markup.Trim());
    }

    [Fact]
    public void Counts_the_changes_and_leads_to_the_restart_on_system()
    {
        var banner = Render<RestartPendingBanner>(parameters => parameters.Add(b => b.Count, 2).Add(b => b.Id, "x"));

        var section = banner.Find("#x");
        Assert.Contains("2", section.TextContent);
        Assert.Equal("/settings/system", section.QuerySelector("a")!.GetAttribute("href"));
    }
}
