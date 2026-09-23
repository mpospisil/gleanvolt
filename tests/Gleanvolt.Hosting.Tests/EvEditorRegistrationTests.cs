using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure.Secrets;
using Gleanvolt.Infrastructure.Vehicles.Skoda;
using Gleanvolt.Infrastructure.Vehicles.VwWebsite;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The other half of <see cref="EvEditorTests"/> (issue #214): what a save is <i>worth</i>. A save
/// writes the configuration the next start reads, so the thing to assert is that the next start reads
/// it — through the same composition root the controller boots on, not through a second model of it.
/// </summary>
public sealed class EvEditorRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-car-" + Guid.NewGuid().ToString("N"));

    public EvEditorRegistrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ISecretStore Secrets => new FileSecretStore(Path.Combine(_root, "data"));

    // The controller's own start: the overrides file and the stored password are added exactly where
    // AddGleanvolt(WebApplicationBuilder) adds them.
    private IServiceCollection Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
        [
            new("Pv:Inverter:Host", "127.0.0.1"),
            new("Pv:Chargers:0:Host", "127.0.0.2"),
            new("Ev:Vehicles:0:Id", "id4"),
        ]);

        builder.Configuration.AddPvSystemOverrides(_root);
        builder.Configuration.AddVehicleAccountPassword(_root);
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services;
    }

    // An editor over the same file and store, as the running process would hold one.
    private EvEditor Editor()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([new("Ev:Vehicles:0:Id", "id4")]);
        configuration.AddPvSystemOverrides(_root);
        configuration.AddVehicleAccountPassword(_root);

        var secrets = Secrets;

        return new EvEditor(
            configuration,
            EvEditor.Snapshot(configuration, secrets),
            secrets,
            6,
            16,
            NullLogger<EvEditor>.Instance);
    }

    private static bool Registers<T>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ImplementationInstance is T || descriptor.ServiceType == typeof(T));

    [Fact]
    public void A_feed_saved_from_the_page_is_the_feed_the_next_start_builds()
    {
        Assert.True(Editor().SaveFeed(VehicleFeed.Volkswagen, new Dictionary<string, string>
        {
            [EvSettingKeys.WebsiteUsername] = "owner@example.com",
            [EvSettingKeys.WebsitePassword] = "hunter2",
            [EvSettingKeys.WebsiteVin] = "WVWZZZE2ZMP000001",
        }).Saved);

        var services = Start();

        // Registered at all, which is what IsConfigured decides -- and it includes the password, so
        // this is also the proof that the secret store reaches the composition root.
        Assert.True(Registers<VwWebsiteOptions>(services));
        Assert.False(Registers<SkodaApiOptions>(services));
    }

    [Fact]
    public void Switching_from_VW_to_Skoda_starts_and_starts_the_other_feed()
    {
        var editor = Editor();

        Assert.True(editor.SaveFeed(VehicleFeed.Volkswagen, new Dictionary<string, string>
        {
            [EvSettingKeys.WebsiteUsername] = "owner@example.com",
            [EvSettingKeys.WebsitePassword] = "hunter2",
            [EvSettingKeys.WebsiteVin] = "WVWZZZE2ZMP000001",
        }).Saved);

        Assert.True(Editor().SaveFeed(VehicleFeed.Skoda, new Dictionary<string, string>
        {
            [EvSettingKeys.SkodaVin] = "TMBJB9NY0M0000001",
        }).Saved);

        // The half-applied switch this exists to prevent: both enabled is a startup exception, and it
        // is the file that has to not say so. Here the key is simply gone rather than written false,
        // because nothing underneath it says true -- the file holds exceptions, and "false" against a
        // default of false is not one. The effective value is what matters, so that is what is read.
        var file = PvSystemOverrides.Read(Path.Combine(_root, "data", "pv-system.json"));
        Assert.DoesNotContain(EvSettingKeys.WebsiteEnabled, file.Keys);
        Assert.Equal("true", file[EvSettingKeys.SkodaEnabled]);
        Assert.Equal(VehicleFeed.Skoda, Editor().Read().Feed);

        // And it starts: the configuration composes without the "both enabled" refusal.
        var services = Start();

        Assert.True(Registers<SkodaApiOptions>(services));
        Assert.False(Registers<VwWebsiteOptions>(services));
    }

    [Fact]
    public void The_make_selects_nothing_at_runtime()
    {
        // The natural-looking mistake this page must not make. The <select> writes the make AND the
        // feed's Enabled key as two separate edits, and only the second is read by anything: a typo
        // in a reported-only field must never change which service polls the car.
        Assert.True(Editor().Save(new Dictionary<string, string>
        {
            [EvSettingKeys.Make] = "Škoda",
            [EvSettingKeys.Model] = "Enyaq 85",
        }).Saved);

        // Said of the file first, because that is the whole claim: naming a make wrote nothing that
        // switches a feed on.
        var file = PvSystemOverrides.Read(Path.Combine(_root, "data", "pv-system.json"));
        Assert.Equal([EvSettingKeys.Make, EvSettingKeys.Model], file.Keys.Order(StringComparer.Ordinal).ToArray());

        // And of the composition root: a Škoda by name is not a Škoda by feed.
        var services = Start();

        Assert.False(Registers<SkodaApiOptions>(services));
        Assert.False(Registers<VwWebsiteOptions>(services));
    }

    [Fact]
    public void A_password_stored_from_the_page_wins_over_a_stale_one_in_the_environment()
    {
        // Otherwise typing a password on /car would silently do nothing on every installation whose
        // .env already carries one -- which is every installation that has ever had this feed working.
        Assert.True(Secrets.Write(SecretNames.VehicleAccountPassword, "from-the-page"));

        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([new(EvSettingKeys.WebsitePassword, "from-dot-env")]);
        configuration.AddVehicleAccountPassword(_root);

        Assert.Equal("from-the-page", configuration[EvSettingKeys.WebsitePassword]);
        Assert.Equal("from-the-page", configuration[EvSettingKeys.DataActPassword]);
    }

    [Fact]
    public void With_nothing_stored_the_environments_password_is_left_exactly_as_it_was()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([new(EvSettingKeys.WebsitePassword, "from-dot-env")]);
        configuration.AddVehicleAccountPassword(_root);

        Assert.Equal("from-dot-env", configuration[EvSettingKeys.WebsitePassword]);
        Assert.Null(configuration[EvSettingKeys.DataActPassword]);
    }
}
