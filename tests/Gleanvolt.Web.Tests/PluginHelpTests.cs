using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Help when a plugin is missing (#470): what each reason the charger can't be controlled links to, and
/// that every manual page linked to exists in each language the manual is written in.
/// </summary>
public class PluginHelpTests
{
    private static ChargerAvailability Unavailable(ChargerAvailabilityState state, string? phraseKey) =>
        new(state, "why", phraseKey is null ? null : Phrase.Of(phraseKey));

    [Fact]
    public void An_available_charger_needs_no_help() =>
        Assert.Null(PluginHelp.ForCharger(ChargerAvailability.Ready));

    [Fact]
    public void No_charger_status_needs_no_help() =>
        Assert.Null(PluginHelp.ForCharger(null));

    [Fact]
    public void No_plugin_links_to_adding_the_charger_and_shows_the_link()
    {
        var help = PluginHelp.ForCharger(Unavailable(ChargerAvailabilityState.NoPlugin, "Charger.NoPlugin"));

        Assert.NotNull(help);
        Assert.Equal(PluginHelp.ChargerPage, help.ManualPage);
        Assert.Null(help.LocalHref);
        Assert.True(help.Prominent);
    }

    [Fact]
    public void A_refused_plugin_links_to_the_turned_away_page()
    {
        var help = PluginHelp.ForCharger(Unavailable(ChargerAvailabilityState.NeedsOwner, "Charger.PluginRefused"));

        Assert.NotNull(help);
        Assert.Equal(PluginHelp.ChargerRefusedPage, help.ManualPage);
        Assert.False(help.Prominent);
    }

    [Theory]
    [InlineData(ChargerAvailabilityState.Error, "Charger.PluginUnhealthy")]
    [InlineData(ChargerAvailabilityState.NeedsOwner, "Charger.PluginUnhealthy")]
    [InlineData(ChargerAvailabilityState.Error, "Charger.Unreadable")]
    [InlineData(ChargerAvailabilityState.Error, null)]
    public void A_plugin_that_cant_reach_the_charger_links_to_its_own_page(ChargerAvailabilityState state, string? key)
    {
        var help = PluginHelp.ForCharger(Unavailable(state, key));

        Assert.NotNull(help);
        Assert.Null(help.ManualPage);
        Assert.Equal(PluginHelp.ChargerSection, help.LocalHref);
    }

    [Fact]
    public void Without_a_phrase_the_state_decides()
    {
        Assert.Equal(PluginHelp.ChargerPage, PluginHelp.ForCharger(Unavailable(ChargerAvailabilityState.NoPlugin, null))?.ManualPage);
        Assert.Equal(PluginHelp.ChargerSection, PluginHelp.ForCharger(Unavailable(ChargerAvailabilityState.NeedsOwner, null))?.LocalHref);
    }

    [Theory]
    [InlineData("charger", PluginHelp.ChargerPage)]
    [InlineData("weather", "setting-up/weather")]
    [InlineData("tariff", PluginHelp.PluginsPage)]
    [InlineData("solar-forecast", PluginHelp.PluginsPage)]
    [InlineData("vehicle", PluginHelp.PluginsPage)]
    [InlineData("something-new", PluginHelp.PluginsPage)]
    public void A_kind_waiting_for_its_plugin_links_to_how_to_start_one(string kind, string page)
    {
        var help = PluginHelp.ForKind(new ExternalProviderStatus(kind, kind, "/providers/" + kind, HasToken: true, ExternalProviderState.Waiting));

        Assert.NotNull(help);
        Assert.Equal(page, help.ManualPage);
    }

    [Fact]
    public void A_disconnected_plugin_links_too()
    {
        var help = PluginHelp.ForKind(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: true, ExternalProviderState.Disconnected));

        Assert.NotNull(help);
        Assert.Equal("setting-up/weather", help.ManualPage);
    }

    [Theory]
    [InlineData(ExternalProviderState.Off)]
    [InlineData(ExternalProviderState.Connected)]
    public void A_kind_turned_off_or_connected_needs_no_help(ExternalProviderState state) =>
        Assert.Null(PluginHelp.ForKind(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: true, state)));

    public static TheoryData<string> LinkedPages() =>
        [PluginHelp.ChargerPage, PluginHelp.ChargerRefusedPage, PluginHelp.PluginsPage, "setting-up/weather"];

    [Theory]
    [MemberData(nameof(LinkedPages))]
    public void Every_linked_manual_page_exists_in_every_language(string page)
    {
        foreach (var language in ManualLinks.Languages)
        {
            var file = Path.Combine(RepoRoot(), "docs", "manual", "src", "content", "docs", language, page + ".mdx");
            Assert.True(File.Exists(file), $"{file} is missing");
        }
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Gleanvolt.slnx not found above " + AppContext.BaseDirectory);
    }
}
