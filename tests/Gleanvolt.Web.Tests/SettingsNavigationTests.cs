using Bunit;
using Microsoft.AspNetCore.Components;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Settings (#255's navigation): the installation and the controller's health behind one header link,
/// as two tabs with addresses of their own -- and the old addresses still answering, for bookmarks.
/// </summary>
public class SettingsNavigationTests : PageTest
{
    private static IEnumerable<string> Routes<TPage>() =>
        typeof(TPage).GetCustomAttributes(typeof(RouteAttribute), inherit: false)
            .Cast<RouteAttribute>()
            .Select(route => route.Template);

    [Fact]
    public void The_installation_is_settings_and_keeps_its_old_address()
    {
        Assert.Equal(["/settings", "/pv-system"], Routes<PvSystem>());
    }

    [Fact]
    public void Health_is_a_settings_tab_and_keeps_its_old_address()
    {
        Assert.Equal(["/settings/health", "/health"], Routes<Health>());
    }

    [Theory]
    [InlineData(SettingsTab.Installation, "/settings")]
    [InlineData(SettingsTab.Health, "/settings/health")]
    public void Marks_the_open_half(SettingsTab current, string href)
    {
        var tabs = Render<SettingsTabs>(parameters => parameters.Add(p => p.Current, current));

        var active = tabs.Find("a.tab.active");
        Assert.Equal(href, active.GetAttribute("href"));
        Assert.Equal("page", active.GetAttribute("aria-current"));
        Assert.Single(tabs.FindAll("a.tab.active"));
    }
}
