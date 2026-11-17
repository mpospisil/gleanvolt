using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Weather;
using Gleanvolt.Infrastructure.OpenWeather;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The weather source (#424): an installation that never chose keeps what it had, a choice is saved for
/// the next start and used at once, and a value that names no source is ignored.
/// </summary>
public sealed class WeatherSourceSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-weather-source-" + Guid.NewGuid().ToString("N"));

    public WeatherSourceSelectionTests() => Directory.CreateDirectory(_root);

    private string OverridesPath => Path.Combine(_root, "overrides.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void WithNoKeyAndNoChoiceThereIsNoWeather()
    {
        var selection = Selection();

        Assert.Equal(WeatherSource.NotProvided, selection.Selected);
        Assert.False(selection.IsChosen);
    }

    [Fact]
    public void AKeyInTheEnvironmentKeepsOpenWeatherAfterTheUpgrade()
    {
        var selection = Selection(("Weather:ApiKey", "env-key"));

        Assert.Equal(WeatherSource.OpenWeather, selection.Selected);
        Assert.False(selection.IsChosen);
    }

    [Fact]
    public void TheShippedPlaceholderIsNoKey() =>
        Assert.Equal(WeatherSource.NotProvided, Selection(("Weather:ApiKey", WeatherKeyStore.PlaceholderKey)).Selected);

    [Fact]
    public void AKeySavedOnSettingsLaterTurnsTheWeatherOnAsItAlwaysDid()
    {
        var secrets = new RecordingSecretStore();
        var selection = Selection(secrets);
        Assert.Equal(WeatherSource.NotProvided, selection.Selected);

        new WeatherKeyStore(secrets, Options.Create(new WeatherOptions())).Save("page-key");

        // The selection's own store reads the secret store at start, so a fresh one stands for the next start;
        // the running one is told through the key store the page uses, below.
        Assert.Equal(WeatherSource.OpenWeather, Selection(secrets).Selected);
    }

    [Fact]
    public void AKeySavedThroughTheRunningStoreIsSeenAtOnce()
    {
        var configuration = Configuration();
        var key = new WeatherKeyStore(new RecordingSecretStore(), Options.Create(new WeatherOptions()));
        var selection = new WeatherSourceSelection(Monitor(configuration), key, configuration, NullLogger<WeatherSourceSelection>.Instance);

        key.Save("page-key");

        Assert.Equal(WeatherSource.OpenWeather, selection.Selected);
    }

    [Theory]
    [InlineData("External", WeatherSource.External)]
    [InlineData("external", WeatherSource.External)]
    [InlineData(" notprovided ", WeatherSource.NotProvided)]
    [InlineData("OPENWEATHER", WeatherSource.OpenWeather)]
    public void TheConfiguredSourceWinsInAnyCase(string value, WeatherSource expected)
    {
        var selection = Selection(("Weather:Source", value), ("Weather:ApiKey", "env-key"));

        Assert.Equal(expected, selection.Selected);
        Assert.True(selection.IsChosen);
    }

    [Theory]
    [InlineData("meteoblue")]
    [InlineData("2")]
    public void ASourceThatNamesNothingIsIgnored(string value)
    {
        var selection = Selection(("Weather:Source", value), ("Weather:ApiKey", "env-key"));

        Assert.Equal(WeatherSource.OpenWeather, selection.Selected);
        Assert.False(selection.IsChosen);
    }

    [Fact]
    public void AChoiceIsSavedForTheNextStartAndUsedAtOnce()
    {
        var selection = Selection(("Weather:ApiKey", "env-key"));
        var changes = 0;
        selection.Changed += () => changes++;

        Assert.True(selection.Select(WeatherSource.External).Saved);

        Assert.Equal(WeatherSource.External, selection.Selected);
        Assert.True(selection.IsChosen);
        Assert.Equal("External", PvSystemOverrides.Read(OverridesPath)["Weather:Source"]);
        Assert.Equal(1, changes);

        // The same again is no change.
        Assert.True(selection.Select(WeatherSource.External).Saved);
        Assert.Equal(1, changes);

        // The next start reads it back.
        Assert.Equal(WeatherSource.External, Selection(("Weather:ApiKey", "env-key")).Selected);
    }

    [Fact]
    public void AChoiceOfNotProvidedHoldsEvenWithAKey()
    {
        var selection = Selection(("Weather:ApiKey", "env-key"));

        Assert.True(selection.Select(WeatherSource.NotProvided).Saved);

        Assert.Equal(WeatherSource.NotProvided, selection.Selected);
    }

    [Fact]
    public void AValueOutsideTheEnumIsRefused()
    {
        var selection = Selection();

        var result = selection.Select((WeatherSource)7);

        Assert.False(result.Saved);
        Assert.Equal("Weather.UnknownSource", result.ProblemPhrases![0]!.Key);
        Assert.False(File.Exists(OverridesPath));
    }

    private WeatherSourceSelection Selection(params (string Key, string Value)[] values) =>
        Selection(new RecordingSecretStore(), values);

    private WeatherSourceSelection Selection(ISecretStore secrets, params (string Key, string Value)[] values)
    {
        var configuration = Configuration(values);
        var options = Monitor(configuration);
        var key = new WeatherKeyStore(secrets, Options.Create(options.CurrentValue));
        return new WeatherSourceSelection(options, key, configuration, NullLogger<WeatherSourceSelection>.Instance);
    }

    // As the host composes it: what it started with, then the overrides file the page writes.
    private IConfigurationRoot Configuration(params (string Key, string Value)[] values)
    {
        var manager = new ConfigurationManager();
        manager.AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value));
        manager[PvSystemOverrides.PathKey] = OverridesPath;
        manager.AddPvSystemOverrides(_root);
        return manager;
    }

    private static IOptionsMonitor<WeatherOptions> Monitor(IConfiguration configuration) =>
        new StaticMonitor(configuration.GetSection(WeatherOptions.SectionName).Get<WeatherOptions>() ?? new WeatherOptions());

    private sealed class StaticMonitor(WeatherOptions value) : IOptionsMonitor<WeatherOptions>
    {
        public WeatherOptions CurrentValue => value;

        public WeatherOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<WeatherOptions, string?> listener) => null;
    }
}
