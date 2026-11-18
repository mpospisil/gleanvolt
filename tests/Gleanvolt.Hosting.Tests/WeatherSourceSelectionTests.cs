using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Weather;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The weather source (#424, #425): NotProvided or External, saved for the next start and used at once;
/// and the move off the removed built-in OpenWeather, which an installation that used it gets at its
/// first start: External chosen, the old key deleted, and a notice until a provider connects.
/// </summary>
public sealed class WeatherSourceSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-weather-source-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSecretStore _secrets = new();

    public WeatherSourceSelectionTests() => Directory.CreateDirectory(_root);

    private string OverridesPath => Path.Combine(_root, "overrides.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void WithNothingConfiguredThereIsNoWeatherAndNothingToSay()
    {
        var selection = Selection();

        Assert.Equal(WeatherSource.NotProvided, selection.Selected);
        Assert.False(selection.BuiltInRetired);
        Assert.False(File.Exists(OverridesPath));
    }

    [Theory]
    [InlineData("External", WeatherSource.External)]
    [InlineData("external", WeatherSource.External)]
    [InlineData(" notprovided ", WeatherSource.NotProvided)]
    public void TheConfiguredSourceIsUsedInAnyCase(string value, WeatherSource expected)
    {
        var selection = Selection(("Weather:Source", value));

        Assert.Equal(expected, selection.Selected);
        Assert.False(selection.BuiltInRetired);
    }

    [Theory]
    [InlineData("meteoblue")]
    [InlineData("1")]
    public void ASourceThatNamesNothingIsIgnored(string value) =>
        Assert.Equal(WeatherSource.NotProvided, Selection(("Weather:Source", value)).Selected);

    [Theory]
    [InlineData("OpenWeather")]
    [InlineData("openweather")]
    public void AChosenBuiltInBecomesExternalWithTheNoticeAndIsRewritten(string value)
    {
        var selection = Selection(("Weather:Source", value));

        Assert.Equal(WeatherSource.External, selection.Selected);
        Assert.True(selection.BuiltInRetired);

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("External", stored[WeatherSourceSelection.Key]);
        Assert.Equal("true", stored[WeatherSourceSelection.RetiredNoticeKey]);
    }

    [Fact]
    public void AKeySavedOnSettingsWithNothingChosenMovesToTheProviderAndTheKeyIsDeleted()
    {
        // Before #424 there was no choice: a key was the built-in in use.
        _secrets.Write(SecretNames.WeatherApiKey, "owm-key");

        var selection = Selection();

        Assert.Equal(WeatherSource.External, selection.Selected);
        Assert.True(selection.BuiltInRetired);
        Assert.Null(_secrets.Read(SecretNames.WeatherApiKey));
    }

    [Fact]
    public void AKeyInTheEnvironmentWithNothingChosenMovesToTheProvider()
    {
        var selection = Selection(("Weather:ApiKey", "env-key"));

        Assert.Equal(WeatherSource.External, selection.Selected);
        Assert.True(selection.BuiltInRetired);
    }

    [Fact]
    public void TheShippedPlaceholderIsNoKey() =>
        Assert.Equal(WeatherSource.NotProvided, Selection(("Weather:ApiKey", "your-openweathermap-api-key")).Selected);

    [Fact]
    public void NotProvidedChosenStaysSoAndTheUnreadKeyIsStillDeleted()
    {
        _secrets.Write(SecretNames.WeatherApiKey, "owm-key");

        var selection = Selection(("Weather:Source", "NotProvided"), ("Weather:ApiKey", "env-key"));

        Assert.Equal(WeatherSource.NotProvided, selection.Selected);
        Assert.False(selection.BuiltInRetired);
        Assert.Null(_secrets.Read(SecretNames.WeatherApiKey));
    }

    [Fact]
    public void TheNoticeOutlivesARestartAndGoesWhenAProviderConnects()
    {
        Selection(("Weather:Source", "OpenWeather"));

        // The next start: the file says External now, and still that the owner hasn't been answered.
        var next = Selection();
        Assert.Equal(WeatherSource.External, next.Selected);
        Assert.True(next.BuiltInRetired);

        var changes = 0;
        next.Changed += () => changes++;
        next.ProviderConnected();

        Assert.False(next.BuiltInRetired);
        Assert.Equal(1, changes);
        Assert.False(PvSystemOverrides.Read(OverridesPath).ContainsKey(WeatherSourceSelection.RetiredNoticeKey));
        Assert.False(Selection().BuiltInRetired);

        // Nothing to clear the second time.
        next.ProviderConnected();
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AChoiceIsSavedForTheNextStartAndUsedAtOnce()
    {
        var selection = Selection();
        var changes = 0;
        selection.Changed += () => changes++;

        Assert.True(selection.Select(WeatherSource.External).Saved);

        Assert.Equal(WeatherSource.External, selection.Selected);
        Assert.Equal("External", PvSystemOverrides.Read(OverridesPath)["Weather:Source"]);
        Assert.Equal(1, changes);

        // The same again is no change.
        Assert.True(selection.Select(WeatherSource.External).Saved);
        Assert.Equal(1, changes);

        // The next start reads it back, with nothing to migrate.
        var next = Selection();
        Assert.Equal(WeatherSource.External, next.Selected);
        Assert.False(next.BuiltInRetired);
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

    private WeatherSourceSelection Selection(params (string Key, string Value)[] values)
    {
        var configuration = Configuration(values);
        return new WeatherSourceSelection(Monitor(configuration), _secrets, configuration, NullLogger<WeatherSourceSelection>.Instance);
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
