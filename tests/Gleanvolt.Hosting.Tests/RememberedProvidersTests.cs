using Gleanvolt.Hosting.Chargers;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.Extensions.Configuration;

namespace Gleanvolt.Hosting.Tests;

/// <summary>The charger plugin Gleanvolt remembers (#440) is kept in the overrides file, beside what else is saved there.</summary>
public sealed class RememberedProvidersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-remembered-" + Guid.NewGuid().ToString("N"));

    private string OverridesPath => Path.Combine(_root, PvSystemOverrides.DefaultPath);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_remembered_plugin_survives_a_restart_and_Forget_removes_only_its_key()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverridesPath)!);
        PvSystemOverrides.Write(OverridesPath, new Dictionary<string, string> { ["Pv:Inverter:Host"] = "192.168.2.10" });

        var first = new RememberedProviders(Start());
        Assert.Null(first.Find(ChargerContract.Kind));
        first.Remember(ChargerContract.Kind, "shelly-topac");

        Assert.Equal("shelly-topac", PvSystemOverrides.Read(OverridesPath)[RememberedProviders.ChargerKey]);
        Assert.Equal("shelly-topac", new RememberedProviders(Start()).Find(ChargerContract.Kind));

        first.Forget(ChargerContract.Kind);

        var stored = PvSystemOverrides.Read(OverridesPath);
        Assert.False(stored.ContainsKey(RememberedProviders.ChargerKey));
        Assert.Equal("192.168.2.10", stored["Pv:Inverter:Host"]);
        Assert.Null(new RememberedProviders(Start()).Find(ChargerContract.Kind));
    }

    [Fact]
    public void Without_an_overrides_file_it_remembers_in_memory()
    {
        var remembered = new RememberedProviders(new ConfigurationBuilder().Build());

        remembered.Remember(ChargerContract.Kind, "shelly-topac");

        Assert.Equal("shelly-topac", remembered.Find(ChargerContract.Kind));
        Assert.False(File.Exists(OverridesPath));
    }

    // The configuration a fresh start reads, with the overrides file as its source.
    private IConfiguration Start()
    {
        var configuration = new ConfigurationManager();
        configuration.AddPvSystemOverrides(_root);
        return configuration;
    }
}
