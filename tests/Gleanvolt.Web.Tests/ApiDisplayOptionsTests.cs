using Gleanvolt.Web;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Where the API section says the API is (issue #142).
///
/// <para>The rule under test: the address is built from the one <em>the browser</em> used to reach the
/// page, not from <c>Web:Port</c>. A hostname or a reverse proxy in front means the configured port is
/// not necessarily the one that works, while the address that just delivered the page demonstrably is —
/// and the whole point of the line is that it can be pasted into an MCP server's configuration on
/// another machine.</para>
/// </summary>
public class ApiDisplayOptionsTests
{
    private static readonly ApiDisplayOptions Enabled = new(
        Enabled: true, "/api/v1", "/api/v1/openapi.json", 8090, [new ApiKeyDisplay("client", null)]);

    [Fact]
    public void KeepsThePortTheBrowserActuallyReachedThePageOn()
    {
        var address = Enabled.AddressFrom("http://gleanvolt.local:8090/");

        Assert.Equal("http://gleanvolt.local:8090/api/v1", address.BaseUrl);
        Assert.Equal("http://gleanvolt.local:8090/api/v1/openapi.json", address.DocumentUrl);
        Assert.False(address.PortIsImplicit);
    }

    [Fact]
    public void SaysWhenTheAddressCarriesNoPortOfItsOwn()
    {
        // A proxy answering on 80 or 443. The URL is correct as it stands, and useless as an answer to
        // "which port?" -- so the page is told to state Web:Port beside it.
        Assert.True(Enabled.AddressFrom("https://gleanvolt.example/").PortIsImplicit);
        Assert.Equal("https://gleanvolt.example/api/v1", Enabled.AddressFrom("https://gleanvolt.example/").BaseUrl);
    }

    [Fact]
    public void KeepsTheSchemeItArrivedOn()
    {
        // A line that says http:// to somebody who reached the page over https is a line that fails
        // when pasted.
        Assert.StartsWith("https://", Enabled.AddressFrom("https://gleanvolt.local:8443/pv-system").BaseUrl);
    }

    [Fact]
    public void IgnoresThePathThePageWasServedFrom()
    {
        // The base URI ends at the application root, but a caller passing the full page URL must not
        // produce /pv-system/api/v1.
        Assert.Equal(
            "http://gleanvolt.local:8090/api/v1",
            Enabled.AddressFrom("http://gleanvolt.local:8090/pv-system").BaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/pv-system")]
    public void FallsBackToThePathsRatherThanGuessingAHost(string? baseUri)
    {
        // Nothing to build an absolute URL from. The relative paths are still true; a guessed host
        // would not be.
        var address = Enabled.AddressFrom(baseUri);

        Assert.Equal("/api/v1", address.BaseUrl);
        Assert.Equal("/api/v1/openapi.json", address.DocumentUrl);
    }

    [Fact]
    public void KeysAreWithheldTogetherOrNotAtAll()
    {
        // The host decides once, for the whole page, so the section can explain it in one place rather
        // than per row.
        Assert.True(Enabled.SecretsWithheld);

        var readable = Enabled with { Keys = [new ApiKeyDisplay("client", "a-real-key")] };
        Assert.False(readable.SecretsWithheld);

        // And an API with no keys at all -- which the host refuses to start -- is not "withheld".
        Assert.False(ApiDisplayOptions.Off.SecretsWithheld);
    }
}
