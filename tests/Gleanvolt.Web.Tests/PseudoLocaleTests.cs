using Xunit.Sdk;

namespace Gleanvolt.Web.Tests;

/// <summary>The pseudo-locale check itself: it has to fail on English, or every page passes.</summary>
public sealed class PseudoLocaleTests
{
    [Fact]
    public void Accepts_text_that_came_from_a_resource() =>
        PseudoLocale.AssertTranslated("<p>[Úpdáťéd [14:00]]</p><td>4.2 kWh</td>");

    [Fact]
    public void Finds_english_left_outside_the_brackets()
    {
        var failure = Assert.Throws<TrueException>(() =>
            PseudoLocale.AssertTranslated("<p>[Úpdáťéd]</p><p>Charging the car</p>"));

        Assert.Contains("Charging", failure.Message);
    }

    [Fact]
    public void Allows_the_pages_own_data() =>
        PseudoLocale.AssertTranslated("<p>[Šíťé]</p><p>Home Roof</p>", "Home Roof");
}
