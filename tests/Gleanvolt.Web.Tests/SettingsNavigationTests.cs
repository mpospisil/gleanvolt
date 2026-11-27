using Bunit;
using Microsoft.AspNetCore.Components;
using Gleanvolt.Web.Components;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Settings (#255's navigation): five tabs behind one header link, in the order of setup (2026-11-27),
/// each with addresses of their own -- and the old addresses still answering, for bookmarks.
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
    public void External_providers_are_a_settings_tab()
    {
        Assert.Equal(["/settings/providers"], Routes<ExternalProvidersSettings>());
    }

    [Fact]
    public void Forecast_and_prices_is_a_settings_tab()
    {
        Assert.Equal(["/settings/forecast-prices"], Routes<ForecastPrices>());
    }

    [Fact]
    public void Integrations_is_a_settings_tab()
    {
        Assert.Equal(["/settings/integrations"], Routes<Integrations>());
    }

    [Fact]
    public void System_is_a_settings_tab_and_keeps_the_health_addresses()
    {
        Assert.Equal(["/settings/system", "/settings/health", "/health"], Routes<Health>());
    }

    [Theory]
    [InlineData(SettingsTab.Installation, "/settings")]
    [InlineData(SettingsTab.ExternalProviders, "/settings/providers")]
    [InlineData(SettingsTab.ForecastPrices, "/settings/forecast-prices")]
    [InlineData(SettingsTab.Integrations, "/settings/integrations")]
    [InlineData(SettingsTab.System, "/settings/system")]
    public void Marks_the_open_tab(SettingsTab current, string href)
    {
        var tabs = Render<SettingsTabs>(parameters => parameters.Add(p => p.Current, current));

        var active = tabs.Find("a.tab.active");
        Assert.Equal(href, active.GetAttribute("href"));
        Assert.Equal("page", active.GetAttribute("aria-current"));
        Assert.Single(tabs.FindAll("a.tab.active"));
    }

    [Fact]
    public void The_tabs_follow_the_order_of_setup()
    {
        var tabs = Render<SettingsTabs>(parameters => parameters.Add(p => p.Current, SettingsTab.System));

        Assert.Equal(
            ["/settings", "/settings/providers", "/settings/forecast-prices", "/settings/integrations", "/settings/system"],
            tabs.FindAll("a.tab").Select(tab => tab.GetAttribute("href")));
    }
}
