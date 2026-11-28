using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Vehicles.External;

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

    // The controller's own start: the overrides file is added exactly where AddGleanvolt(WebApplicationBuilder) adds it.
    private IServiceCollection Start(params KeyValuePair<string, string?>[] environment)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
        [
            .. UsableSite.Pairs,
            new("Ev:Vehicles:0:Id", "id4"),
            .. environment,
        ]);

        builder.Configuration.AddPvSystemOverrides(_root);
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services;
    }

    // An editor over the same file, as the running process would hold one.
    private EvEditor Editor()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([new("Ev:Vehicles:0:Id", "id4")]);
        configuration.AddPvSystemOverrides(_root);

        return new EvEditor(configuration, EvEditor.Snapshot(configuration), 6, 16, NullLogger<EvEditor>.Instance);
    }

    private static bool Registers<T>(IServiceCollection services) =>
        services.Any(descriptor => descriptor.ImplementationInstance is T || descriptor.ServiceType == typeof(T));

    [Fact]
    public void A_plugin_chosen_on_the_page_is_the_feed_the_next_start_builds()
    {
        Assert.True(Editor().SaveFeed(VehicleFeed.External, new Dictionary<string, string>()).Saved);

        Assert.True(Registers<ExternalVehicleFeed>(Start()));
    }

    [Fact]
    public void The_make_selects_nothing_at_runtime()
    {
        // The natural-looking mistake this page must not make: a typo in a reported-only field must never change
        // which feed reads the car.
        Assert.True(Editor().Save(new Dictionary<string, string>
        {
            [EvSettingKeys.Make] = "Škoda",
            [EvSettingKeys.Model] = "Enyaq 85",
        }).Saved);

        var file = PvSystemOverrides.Read(Path.Combine(_root, "data", "pv-system.json"));
        Assert.Equal([EvSettingKeys.Make, EvSettingKeys.Model], file.Keys.Order(StringComparer.Ordinal).ToArray());

        Assert.False(Registers<ExternalVehicleFeed>(Start()));
    }

    [Theory]
    [InlineData("Vehicle:Website:Enabled")]
    [InlineData("Vehicle:Skoda:Enabled")]
    [InlineData("Vehicle:DataAct:Enabled")]
    [InlineData("VW_ENABLED")]
    public void A_built_in_feed_left_enabled_starts_the_plugin_feed(string flag)
    {
        // #477: the first start after the upgrade already reads the car through the plugin, before the overrides
        // file the move writes has been read back.
        Assert.True(Registers<ExternalVehicleFeed>(Start(KeyValuePair.Create<string, string?>(flag, "true"))));
        Assert.Equal(VehicleFeed.External, Snapshot(KeyValuePair.Create<string, string?>(flag, "true")).Feed);
    }

    [Fact]
    public void A_built_in_feed_moved_before_leaves_the_owners_later_choice_alone()
    {
        // Moved once: an owner who then picks charge only is not moved back by a .env that still says so.
        var path = Path.Combine(_root, "data", "pv-system.json");
        PvSystemOverrides.Write(path, new Dictionary<string, string> { [Vehicles.RetiredCarFeed.MovedKey] = "volkswagen" });

        Assert.False(Registers<ExternalVehicleFeed>(Start(KeyValuePair.Create<string, string?>("Vehicle:Website:Enabled", "true"))));
    }

    private EvRunningState Snapshot(KeyValuePair<string, string?> environment)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection([environment]);
        configuration.AddPvSystemOverrides(_root);
        return EvEditor.Snapshot(configuration);
    }
}
