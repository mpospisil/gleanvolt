using System.Globalization;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>The helpers a translated sentence goes through (#290).</summary>
public sealed class LocalizedTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    [Fact]
    public void Says_a_phrase_with_its_arguments_written_by_the_ui()
    {
        var phrase = Phrase.Of(
            "SolarGrid.Clears",
            new PhraseArg.Power(1_500),
            new PhraseArg.Clock(new DateTimeOffset(2026, 8, 12, 8, 30, 0, TimeSpan.Zero)),
            new PhraseArg.Clock(new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero)));

        Assert.Equal("forecast surplus clears 1.5 kW from 10:30 until 17:00", Localized.Say(phrase, "the service's own", Prague));
    }

    [Fact]
    public void Falls_back_to_the_services_english_for_a_key_it_does_not_know() =>
        Assert.Equal("the service's own", Localized.Say(Phrase.Of("No.Such.Key"), "the service's own", Prague));

    [Fact]
    public void Falls_back_to_the_services_english_without_a_phrase() =>
        Assert.Equal("the service's own", Localized.Say(null, "the service's own", Prague));

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 2, "other")]
    [InlineData("en", 5, "other")]
    [InlineData("cs", 1, "one")]
    [InlineData("cs", 3, "few")]
    [InlineData("cs", 5, "other")]
    [InlineData("cs", 0, "other")]
    public void Picks_the_plural_form_the_language_uses(string language, long count, string expected)
    {
        var ui = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        try
        {
            Assert.Equal(expected, Localized.Plural(count, "one", "few", "other"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = ui;
        }
    }

    [Fact]
    public void Formats_numbers_in_the_viewers_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("cs");
        try
        {
            Assert.Equal("6,0 kWh", Units.Kwh(6_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
}
