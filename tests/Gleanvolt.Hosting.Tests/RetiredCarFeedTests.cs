using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Vehicles;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The move off the removed built-in car feeds (#477): an installation that used one gets <c>Vehicle:External</c> at
/// its first start, with a notice naming the plugin until a car plugin connects; the built-ins' secrets are deleted at
/// every start, whatever feed is chosen; and the old flags are ignored with a warning, not refused.
/// </summary>
public sealed class RetiredCarFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-retired-car-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSecretStore _secrets = new();
    private readonly Logger _log = new();

    public RetiredCarFeedTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string OverridesPath => Path.Combine(_root, "overrides.json");

    [Fact]
    public void WithNoBuiltInThereIsNothingToMoveOrSay()
    {
        var retired = Retired();

        Assert.Null(retired.Notice);
        Assert.False(File.Exists(OverridesPath));
        Assert.Empty(_log.Warnings);
    }

    [Theory]
    [InlineData("Vehicle:Website:Enabled", "volkswagen")]
    [InlineData("Vehicle:Skoda:Enabled", "skoda")]
    [InlineData("Vehicle:DataAct:Enabled", "vwgroup")]
    [InlineData("VW_ENABLED", "vwgroup")]
    public void ABuiltInMovesToThePluginWithTheNotice(string flag, string plugin)
    {
        var retired = Retired((flag, "true"));

        Assert.Equal(plugin, retired.Notice?.Id);

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.Equal("true", stored[EvSettingKeys.ExternalEnabled]);
        Assert.Equal(plugin, stored[RetiredCarFeed.MovedKey]);
        Assert.Equal("true", stored[RetiredCarFeed.NoticeKey]);

        Assert.Contains(_log.Warnings, warning => warning.Contains("built-in car feeds are gone", StringComparison.Ordinal));
    }

    [Fact]
    public void TheWebsiteBesideThePortalNamesTheVolkswagenPlugin()
    {
        // The Pi's .env: VW_WEBSITE_ENABLED and VW_ENABLED both, where volkswagen.de was the feed that ran.
        var retired = Retired(("Vehicle:Website:Enabled", "true"), ("Vehicle:DataAct:Enabled", "true"));

        Assert.Equal(CarFeedPlugin.Volkswagen, retired.Notice);
    }

    [Fact]
    public void AMoveHappensOnceAndTheNoticeStaysUntilAPluginConnects()
    {
        Retired(("Vehicle:Skoda:Enabled", "true"));

        // The next start: moved before, the notice still on.
        var again = Retired(("Vehicle:Skoda:Enabled", "true"));
        Assert.Equal(CarFeedPlugin.Skoda, again.Notice);

        var changed = 0;
        again.Changed += () => changed++;
        again.ProviderConnected();

        Assert.Null(again.Notice);
        Assert.Equal(1, changed);
        Assert.DoesNotContain(RetiredCarFeed.NoticeKey, PvSystemOverrides.Read(OverridesPath).Keys);

        // And it stays gone, with the plugin still the feed.
        Assert.Null(Retired(("Vehicle:Skoda:Enabled", "true")).Notice);
        Assert.Equal("true", PvSystemOverrides.Read(OverridesPath)[EvSettingKeys.ExternalEnabled]);
    }

    [Fact]
    public void AFeedChosenAfterTheMoveIsNotUndone()
    {
        Retired(("Vehicle:Website:Enabled", "true"));

        // The owner picks charge only on the Car page; .env still says VW_WEBSITE_ENABLED=true.
        var stored = PvSystemOverrides.Read(OverridesPath);
        stored.Remove(EvSettingKeys.ExternalEnabled);
        PvSystemOverrides.Write(OverridesPath, stored);

        Retired(("Vehicle:Website:Enabled", "true"));

        Assert.DoesNotContain(EvSettingKeys.ExternalEnabled, PvSystemOverrides.Read(OverridesPath).Keys);
    }

    [Fact]
    public void TheOldFlagsAreIgnoredWithAWarningAtEveryStart()
    {
        Retired(("Vehicle:Website:Enabled", "true"), ("VW_ENABLED", "true"));
        _log.Warnings.Clear();

        Retired(("Vehicle:Website:Enabled", "true"), ("VW_ENABLED", "true"));

        var warning = Assert.Single(_log.Warnings);
        Assert.Contains("VW_WEBSITE_ENABLED, VW_ENABLED are set", warning, StringComparison.Ordinal);
        Assert.Contains("GLEANVOLT_PLUGINS", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlagThatIsFalseSaysNothing()
    {
        // Compose passes every flag, false by default.
        Retired(("Vehicle:Website:Enabled", "false"), ("Vehicle:Skoda:Enabled", "false"), ("Vehicle:DataAct:Enabled", "false"));

        Assert.Empty(_log.Warnings);
        Assert.False(File.Exists(OverridesPath));
    }

    [Fact]
    public void TheBuiltInsSecretsAreDeletedWhateverFeedIsChosen()
    {
        // An owner already on a plugin, with the old password and session still in the store.
        foreach (var name in SecretNames.RetiredVehicleSecrets)
        {
            _secrets.Stored[name] = "secret";
        }

        _secrets.Stored[SecretNames.ProviderToken] = "token";

        Retired((EvSettingKeys.ExternalEnabled, "true"));

        Assert.Equal(SecretNames.RetiredVehicleSecrets, _secrets.Deleted);
        Assert.Equal("token", _secrets.Read(SecretNames.ProviderToken));
        Assert.All(_log.Messages, message => Assert.DoesNotContain("secret", message.Replace("secret store", string.Empty), StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAnOverridesFileTheMoveIsForThisRun()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([new("Vehicle:Website:Enabled", "true")]);

        var retired = new RetiredCarFeed(configuration, _secrets, _log);

        Assert.Equal(CarFeedPlugin.Volkswagen, retired.Notice);
        Assert.Contains(_log.Warnings, warning => warning.Contains("for this run", StringComparison.Ordinal));
    }

    private RetiredCarFeed Retired(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value));
        configuration[PvSystemOverrides.PathKey] = OverridesPath;
        configuration.AddPvSystemOverrides(_root);
        return new RetiredCarFeed(configuration, _secrets, _log);
    }

    private sealed class Logger : ILogger<RetiredCarFeed>
    {
        public List<string> Messages { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            Messages.Add(message);

            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(message);
            }
        }
    }
}
