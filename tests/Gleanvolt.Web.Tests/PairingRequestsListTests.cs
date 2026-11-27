using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>Plugins that knock and wait: the owner sees each one's code and accepts or rejects it.</summary>
public class PairingRequestsListTests : PageTest
{
    private readonly FakeExternalProviders _providers = new();
    private readonly FakeTimeProvider _time = new();

    public PairingRequestsListTests()
    {
        Services.AddSingleton<IExternalProviders>(_providers);
        Services.AddSingleton<TimeProvider>(_time);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PairingRequestView Request(string id = "r1", bool local = true, PairingBlock block = PairingBlock.None) =>
        new(id, "charger", "acme-wallbox", "Acme Wallbox", "1.0", local ? "127.0.0.1" : "192.168.1.50", local, "4821", DateTimeOffset.UnixEpoch, block);

    [Fact]
    public void A_waiting_plugin_is_listed_with_its_code()
    {
        _providers.Waiting.Add(Request());

        var row = Render<PairingRequestsList>().Find("#pairing-r1");

        Assert.Contains("Acme Wallbox", row.TextContent);
        Assert.Contains("acme-wallbox", row.TextContent);
        Assert.Equal("4821", row.QuerySelector(".pairing-code")!.TextContent);
        Assert.Empty(row.QuerySelectorAll("a, iframe"));
    }

    [Fact]
    public void A_plugin_on_this_machine_says_so_and_has_no_hint()
    {
        _providers.Waiting.Add(Request());

        var row = Render<PairingRequestsList>().Find("#pairing-r1");

        Assert.Equal("This machine", row.QuerySelector(".pairing-source")!.TextContent);
        Assert.Null(row.QuerySelector(".pairing-hint"));
    }

    [Fact]
    public void A_plugin_elsewhere_shows_the_code_hint()
    {
        _providers.Waiting.Add(Request(local: false));

        var row = Render<PairingRequestsList>().Find("#pairing-r1");

        Assert.Equal("192.168.1.50", row.QuerySelector(".pairing-source")!.TextContent);
        Assert.Contains("check that the code matches", row.QuerySelector(".pairing-hint")!.TextContent);
    }

    [Fact]
    public void Accept_calls_the_registry()
    {
        _providers.Waiting.Add(Request());
        var page = Render<PairingRequestsList>();

        page.Find("#pairing-r1 .pairing-accept").Click();

        Assert.Equal(["r1"], _providers.Accepted);
    }

    [Fact]
    public void Reject_calls_the_registry()
    {
        _providers.Waiting.Add(Request());
        var page = Render<PairingRequestsList>();

        page.Find("#pairing-r1 .pairing-reject").Click();

        Assert.Equal(["r1"], _providers.Rejected);
        Assert.Empty(page.FindAll("#pairing-requests"));
    }

    [Theory]
    [InlineData(PairingBlock.Duplicate, "Two plugins ask with this name")]
    [InlineData(PairingBlock.KindOff, "This kind of provider is turned off.")]
    [InlineData(PairingBlock.AnotherProvider, "already uses another charger plugin")]
    public void A_blocked_request_cannot_be_accepted_and_says_why(PairingBlock block, string why)
    {
        _providers.Waiting.Add(Request(block: block));

        var row = Render<PairingRequestsList>().Find("#pairing-r1");

        Assert.True(row.QuerySelector(".pairing-accept")!.HasAttribute("disabled"));
        Assert.Contains(why, row.QuerySelector(".pairing-block")!.TextContent);
    }

    [Fact]
    public void An_unblocked_request_says_nothing_about_blocking()
    {
        _providers.Waiting.Add(Request());

        var row = Render<PairingRequestsList>().Find("#pairing-r1");

        Assert.False(row.QuerySelector(".pairing-accept")!.HasAttribute("disabled"));
        Assert.Null(row.QuerySelector(".pairing-block"));
    }

    [Fact]
    public void Nothing_is_shown_when_nobody_waits()
    {
        Assert.Equal("", Render<PairingRequestsList>().Markup.Trim());
    }

    [Fact]
    public void A_request_that_arrives_appears()
    {
        var page = Render<PairingRequestsList>();

        _providers.Waiting.Add(Request());
        _providers.RaiseChanged();

        page.WaitForAssertion(() => Assert.NotNull(page.Find("#pairing-r1")));
    }

    [Fact]
    public void A_failed_accept_says_so()
    {
        _providers.Waiting.Add(Request());
        _providers.AcceptFails = true;
        var page = Render<PairingRequestsList>();

        page.Find("#pairing-r1 .pairing-accept").Click();

        Assert.Contains("could not be saved", page.Find(".pairing-message").TextContent);
        Assert.NotNull(page.Find("#pairing-r1"));
    }

    [Fact]
    public void An_accept_that_came_too_late_says_so()
    {
        _providers.Waiting.Add(Request());
        _providers.AcceptResult = false;
        var page = Render<PairingRequestsList>();

        page.Find("#pairing-r1 .pairing-accept").Click();

        Assert.Contains("no longer waiting", page.Find(".pairing-message").TextContent);
    }

    [Fact]
    public void A_request_that_expires_without_a_change_notice_disappears_on_the_next_read()
    {
        _providers.Waiting.Add(Request());
        var page = Render<PairingRequestsList>();

        _providers.Waiting.Clear();
        Assert.NotNull(page.Find("#pairing-r1"));
        _time.Advance(TimeSpan.FromSeconds(5));

        page.WaitForAssertion(() => Assert.Empty(page.FindAll("#pairing-r1")));
    }
}
