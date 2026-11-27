using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The band the layout carries on every page while a plugin waits to be paired. The list with Accept lives on
/// Settings → External providers, a tab nobody opens unprompted; a plugin waits there until someone does, so
/// the band says so wherever the owner is, and links to it.
/// </summary>
public class PairingAlertTests : PageTest
{
    private readonly FakeExternalProviders _providers = new();
    private readonly FakeTimeProvider _time = new();

    public PairingAlertTests()
    {
        Services.AddSingleton<IExternalProviders>(_providers);
        Services.AddSingleton<TimeProvider>(_time);
    }

    private static PairingRequestView Request(string id = "r1", bool local = true, string name = "SolaX EV charger") =>
        new(id, "charger", "solax-charger", name, "1.0", local ? "127.0.0.1" : "192.168.2.40", local, "494 506", DateTimeOffset.UnixEpoch, PairingBlock.None);

    [Fact]
    public void Nothing_is_shown_while_nobody_waits()
    {
        Assert.Empty(Render<PairingAlert>().Markup.Trim());
    }

    [Fact]
    public void One_plugin_on_this_machine_is_named_and_linked_to_settings()
    {
        _providers.Waiting.Add(Request());

        var alert = Render<PairingAlert>().Find("#pairing-alert");

        Assert.Equal("alert", alert.GetAttribute("role"));
        Assert.Contains("A plugin asks to connect: SolaX EV charger (this machine).", alert.TextContent);
        Assert.Equal("/settings/providers", alert.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Open Settings → External providers", alert.QuerySelector("a")!.TextContent);
    }

    [Fact]
    public void One_plugin_elsewhere_says_its_address()
    {
        _providers.Waiting.Add(Request(local: false));

        var alert = Render<PairingAlert>().Find("#pairing-alert");

        Assert.Contains("SolaX EV charger (192.168.2.40).", alert.TextContent);
    }

    [Fact]
    public void Several_plugins_are_counted()
    {
        _providers.Waiting.Add(Request("r1"));
        _providers.Waiting.Add(Request("r2", local: false, name: "Shelly TopAC"));

        var alert = Render<PairingAlert>().Find("#pairing-alert");

        Assert.Contains("Plugins asking to connect: 2.", alert.TextContent);
        Assert.DoesNotContain("Shelly", alert.TextContent);
    }

    [Fact]
    public void Accepting_stays_on_settings()
    {
        // One place to accept, beside the code check; and never a link to the plugin's own page.
        _providers.Waiting.Add(Request());

        var alert = Render<PairingAlert>().Find("#pairing-alert");

        Assert.Empty(alert.QuerySelectorAll("button, iframe"));
        Assert.Single(alert.QuerySelectorAll("a"));
    }

    [Fact]
    public void The_providers_page_itself_carries_no_band()
    {
        _providers.Waiting.Add(Request());
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/providers");

        Assert.Empty(Render<PairingAlert>().Markup.Trim());
    }

    [Fact]
    public void A_knock_while_the_page_is_open_shows_the_band()
    {
        var cut = Render<PairingAlert>();

        _providers.Waiting.Add(Request());
        _providers.RaiseChanged();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#pairing-alert")));
    }

    [Fact]
    public void A_request_that_expires_without_a_change_notice_goes_on_the_next_read()
    {
        _providers.Waiting.Add(Request());
        var cut = Render<PairingAlert>();

        _providers.Waiting.Clear();
        _time.Advance(TimeSpan.FromSeconds(5));

        cut.WaitForAssertion(() => Assert.Empty(cut.Markup.Trim()));
    }

    [Fact]
    public void The_band_speaks_czech_for_czech()
    {
        _providers.Waiting.Add(Request());

        var ui = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("cs");

        try
        {
            var alert = Render<PairingAlert>().Find("#pairing-alert");

            Assert.Contains("Plugin žádá o připojení: SolaX EV charger (tento počítač).", alert.TextContent);
            Assert.Contains("Externí zdroje dat", alert.QuerySelector("a")!.TextContent);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = ui;
        }
    }
}
