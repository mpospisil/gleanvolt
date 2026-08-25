using Microsoft.Extensions.Configuration;
using Gleanvolt.Hosting.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Keys that have moved (issue #111). Each of them decided something real, so the failure a silent
/// ignore produces is the invisible kind: a controller that starts, polls and charges — against a
/// default address, while the operator's file names another one.
/// </summary>
public class RetiredConfigurationKeysTests
{
    private static void Refuse(params (string Key, string? Value)[] settings) =>
        RetiredConfigurationKeys.Refuse(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Theory]
    [InlineData("Solax:Inverter:Host", "Pv:Inverter")]
    [InlineData("Solax:EvCharger:Host", "Pv:Chargers:0")]
    [InlineData("Solax:PollIntervalSeconds", "Controller:PollIntervalSeconds")]
    [InlineData("Weather:Latitude", "Pv:Latitude")]
    [InlineData("Weather:Longitude", "Pv:Longitude")]
    [InlineData("HomeAssistant:DeviceName", "Pv:Name")]
    public void AKeyThatHasMovedStopsTheHostAndNamesItsReplacement(string retired, string replacement)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Refuse((retired, "192.168.2.10")));

        Assert.Contains(retired.Split(':')[0], error.Message);
        Assert.Contains(replacement, error.Message);
    }

    [Fact]
    public void TheMessageIsInTheSpellingAnOperatorHasToEdit()
    {
        // The key is nearly always set as an environment variable or a .env line, not as JSON, so the
        // message that only says "Solax:Inverter" makes the reader translate it themselves.
        var error = Assert.Throws<InvalidOperationException>(() => Refuse(("Solax:Inverter:Host", "192.168.2.10")));

        Assert.Contains("Solax__Inverter", error.Message);
        Assert.Contains("Pv__Inverter", error.Message);
    }

    [Fact]
    public void EveryRetiredKeyIsReportedAtOnce()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Refuse(
            ("Solax:Inverter:Host", "192.168.2.10"),
            ("Solax:EvCharger:Host", "192.168.2.6"),
            ("Weather:Latitude", "49.2678")));

        Assert.Contains("Solax__Inverter", error.Message);
        Assert.Contains("Solax__EvCharger", error.Message);
        Assert.Contains("Weather__Latitude", error.Message);
    }

    [Fact]
    public void TheKeysThatDidNotMoveAreLeftAlone()
    {
        // A provider's key and a provider's handle for your roof belong beside that provider's other
        // settings; only the description of the array itself moved.
        Refuse(
            ("Weather:ApiKey", "key"),
            ("Weather:Units", "metric"),
            ("Solcast:ResourceId", "abcd-1234"),
            ("Controller:PollIntervalSeconds", "5"),
            ("Pv:Inverter:Host", "192.168.2.10"));
    }

    [Fact]
    public void ConfigurationThatMentionsNoneOfThemPasses()
    {
        Refuse();
    }
}
