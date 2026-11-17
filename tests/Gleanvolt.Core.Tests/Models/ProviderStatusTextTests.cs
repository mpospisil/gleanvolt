using System.Globalization;
using System.Text.Json;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Models;

/// <summary>An external provider's status, worded for every language (#415).</summary>
public class ProviderStatusTextTests
{
    private static readonly Dictionary<string, string> Both = new() { ["en"] = "The account changed.", ["cs"] = "Účet se změnil." };

    [Fact]
    public void A_known_code_is_Gleanvolts_own_phrase()
    {
        var said = ProviderStatusText.Of("needs-sign-in", null, null)!.Value;

        Assert.Equal(Phrase.Of("ProviderStatus.NeedsSignIn"), said.Phrase);
        Assert.Equal("sign in on the provider's page", said.English);
    }

    [Fact]
    public void A_code_with_detail_says_both_and_leaves_out_the_old_hosts_sentence()
    {
        var said = ProviderStatusText.Of("needs-sign-in", Both, "The account changed. Sign in again on the plugin's page.")!.Value;

        Assert.Equal("ProviderStatus.WithDetail", said.Phrase!.Key);
        Assert.Equal(new PhraseArg.Said(Phrase.Of("ProviderStatus.NeedsSignIn"), "sign in on the provider's page"), said.Phrase.Args[0]);
        Assert.Equal(new PhraseArg.Translations(Both, "The account changed."), said.Phrase.Args[1]);
        Assert.Equal("sign in on the provider's page. The account changed.", said.English);
    }

    [Fact]
    public void A_code_with_only_the_old_hosts_sentence_is_the_code_alone()
    {
        var said = ProviderStatusText.Of("unreachable", null, "OTE is down; see the provider.")!.Value;

        Assert.Equal(Phrase.Of("ProviderStatus.Unreachable"), said.Phrase);
    }

    [Fact]
    public void Without_a_code_the_message_is_said_as_it_was()
    {
        var said = ProviderStatusText.Of(null, null, "OTE is down.")!.Value;

        Assert.Equal(Phrase.Of("ProviderStatus.Detail", new PhraseArg.Text("OTE is down.")), said.Phrase);
        Assert.Equal("OTE is down.", said.English);
    }

    [Fact]
    public void An_unknown_code_is_said_as_no_code()
    {
        var said = ProviderStatusText.Of("needs-a-pigeon", Both, null)!.Value;

        Assert.Equal(Phrase.Of("ProviderStatus.Detail", new PhraseArg.Translations(Both, "The account changed.")), said.Phrase);
    }

    [Fact]
    public void Nothing_said_is_null() => Assert.Null(ProviderStatusText.Of(null, null, null));

    [Theory]
    [InlineData("cs", "Účet se změnil.")]
    [InlineData("cs-CZ", "Účet se změnil.")]
    [InlineData("en-GB", "The account changed.")]
    [InlineData("de", "The account changed.")]
    public void Translations_pick_the_viewers_language_then_English(string culture, string expected) =>
        Assert.Equal(expected, new PhraseArg.Translations(Both, "fallback").For(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Translations_without_English_fall_back_to_the_given_English()
    {
        var czechOnly = new PhraseArg.Translations(new Dictionary<string, string> { ["CS-cz"] = "Jen česky." }, "Only English.");

        Assert.Equal("Jen česky.", czechOnly.For(CultureInfo.GetCultureInfo("cs-CZ")));
        Assert.Equal("Only English.", czechOnly.For(CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void Translations_compare_by_value_and_survive_json()
    {
        var phrase = Phrase.Of("ProviderStatus.Detail", new PhraseArg.Translations(Both, "The account changed."));
        var copy = Phrase.Of("ProviderStatus.Detail", new PhraseArg.Translations(new Dictionary<string, string>(Both), "The account changed."));

        Assert.Equal(phrase, copy);
        Assert.Equal(phrase.GetHashCode(), copy.GetHashCode());
        Assert.Equal(phrase, JsonSerializer.Deserialize<Phrase>(JsonSerializer.Serialize(phrase)));
    }
}
