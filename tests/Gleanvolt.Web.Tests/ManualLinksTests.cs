using System.Globalization;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Links into the user manual (#329) open the pages for the running version, in the viewer's language.
/// </summary>
public class ManualLinksTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Czech = CultureInfo.GetCultureInfo("cs");

    [Theory]
    [InlineData("1.4.2 (31bf347)", "v1.4")]
    [InlineData("1.10.0", "v1.10")]
    [InlineData("2.0.0-rc.1 (abc1234)", "v2.0")]
    [InlineData("0.0.0-dev", "next")]
    [InlineData("a local build", "latest")]
    public void A_release_links_to_its_own_minor_version(string version, string folder)
    {
        Assert.Equal(folder, ManualLinks.Version(new WebBuildInfo(version)));
    }

    [Fact]
    public void No_build_information_links_to_the_newest_release()
    {
        Assert.Equal("https://mpospisil.github.io/gleanvolt/latest/en/", ManualLinks.For("", null, English));
    }

    [Fact]
    public void A_page_in_czech_for_a_release()
    {
        Assert.Equal(
            "https://mpospisil.github.io/gleanvolt/v1.4/cs/how-it-works/unmetered-phase/",
            ManualLinks.For("/how-it-works/unmetered-phase", new WebBuildInfo("1.4.2"), Czech));
    }

    [Fact]
    public void A_language_the_manual_lacks_falls_back_to_english()
    {
        Assert.Equal(
            "https://mpospisil.github.io/gleanvolt/v1.4/en/glossary/",
            ManualLinks.For("glossary", new WebBuildInfo("1.4.2"), CultureInfo.GetCultureInfo("de")));
    }
}
