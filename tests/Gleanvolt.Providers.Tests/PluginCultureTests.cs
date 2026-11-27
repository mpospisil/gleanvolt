using Gleanvolt.Chargers;
using Xunit;

namespace Gleanvolt.Providers.Tests;

public sealed class PluginCultureTests
{
    [Theory]
    [InlineData("cs", null, "cs")]
    [InlineData("cs-CZ", null, "cs-CZ")]
    [InlineData("de", "c=cs|uic=cs", "de")]      // the query wins over the cookie
    [InlineData("xx-!!", "c=cs|uic=cs", "en")]   // an unusable query is English, not the cookie's
    [InlineData("", "c=cs|uic=cs", "en")]
    [InlineData(null, "c=cs|uic=cs", "cs")]      // no query: the cookie (the circuit's requests)
    [InlineData(null, "c=zz|uic=zz", "en")]
    [InlineData(null, null, "en")]
    public void ChoosesTheQueryThenTheCookieThenEnglish(string? query, string? cookie, string expected) =>
        Assert.Equal(expected, PluginCulture.Choose(query, cookie).Name);

    [Fact]
    public void ALongQueryIsEnglish() => Assert.Equal("en", PluginCulture.Choose(new string('a', 300), null).Name);
}
