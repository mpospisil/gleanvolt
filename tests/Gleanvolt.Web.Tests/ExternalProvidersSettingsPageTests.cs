using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Settings' External providers tab: the section that used to sit above both other tabs, on a page of
/// its own, with its links leading back to the Installation tab.
/// </summary>
public class ExternalProvidersSettingsPageTests : PageTest
{
    private readonly FakeExternalProviders _providers = new();

    public ExternalProvidersSettingsPageTests()
    {
        Services.AddSingleton<IExternalProviders>(_providers);
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        Services.AddSingleton(Options.Create(new WebOptions()));
        Services.AddSingleton(new Gleanvolt.Web.Auth.WebPasswordState(null));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Shows_the_section_under_its_own_tab()
    {
        _providers.Set(new ExternalProviderStatus("charger", "Charger", "/providers/charger", HasToken: false, ExternalProviderState.Waiting));

        var page = Render<ExternalProvidersSettings>();

        Assert.Equal("/settings/providers", page.Find(".settings-tabs a.tab.active").GetAttribute("href"));
        Assert.True(page.Markup.IndexOf("settings-tabs", StringComparison.Ordinal)
            < page.Markup.IndexOf("id=\"external-providers\"", StringComparison.Ordinal));
        Assert.Equal("/settings#charger-editor", page.Find("#external-providers-charger a").GetAttribute("href"));
    }
}
