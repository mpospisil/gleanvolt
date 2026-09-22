using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Infrastructure.Secrets;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Where the composition root puts the secret store (issue #215): one, always, whether or not this
/// installation has a car — <c>/health</c> and the startup log name it either way — and pointed at the
/// data directory rather than at the read-only content root the .deb installs into.
/// </summary>
public sealed class SecretStoreRegistrationTests : IDisposable
{
    private readonly string _data = Path.Combine(
        Path.GetTempPath(), $"gleanvolt-secret-registration-{Guid.NewGuid():N}");

    [Fact]
    public async Task A_store_is_registered_even_with_no_car_configured()
    {
        await using var provider = Build().BuildServiceProvider();

        var store = provider.GetRequiredService<ISecretStore>();

        Assert.IsType<FileSecretStore>(store);
        Assert.Equal(_data, ((FileSecretStore)store).Directory);
    }

    /// <summary>
    /// The relative default has to mean the content root, not the working directory: the .deb's unit
    /// starts the service from <c>/var/lib/gleanvolt</c> with its content root in a read-only
    /// <c>/opt/gleanvolt</c>, and the SQLite stores and <c>pv-system.json</c> already follow this rule.
    /// </summary>
    [Fact]
    public async Task A_relative_directory_resolves_against_the_content_root()
    {
        await using var provider = Build(("Secrets:Directory", "data")).BuildServiceProvider();

        var store = Assert.IsType<FileSecretStore>(provider.GetRequiredService<ISecretStore>());

        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "data"), store.Directory);
    }

    /// <summary>The choice, not just its result: the startup log says which store and why.</summary>
    [Fact]
    public async Task The_choice_is_registered_with_the_reason_the_startup_log_prints()
    {
        await using var provider = Build().BuildServiceProvider();

        var choice = provider.GetRequiredService<SecretStoreChoice>();

        Assert.NotEmpty(choice.Reason);
        Assert.Equal(OperatingSystem.IsWindows() && !SecretStoreSelection.RunningInContainer(), choice.Kind == SecretStoreKind.Dpapi);
    }

    /// <summary>
    /// The two settings the secret store replaced. Refused rather than ignored: reading secrets from
    /// somewhere else than the operator's file says would cost a fresh sign-in with nothing in the log.
    /// </summary>
    [Theory]
    [InlineData("Vehicle:Website:SessionPath")]
    [InlineData("Vehicle:Skoda:KeyPath")]
    public void The_settings_it_replaced_are_refused_at_startup(string retired)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Build((retired, "/var/lib/gleanvolt/somewhere.json")));

        Assert.Contains(retired, error.Message);
        Assert.Contains("Secrets:Directory", error.Message);
    }

    private IServiceCollection Build(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Configuration.AddInMemoryCollection(
            new (string Key, string? Value)[]
            {
                ("Pv:Inverter:Host", "127.0.0.1"),
                ("Pv:Chargers:0:Host", "127.0.0.1"),
                ("Secrets:Directory", _data),
            }
            .Concat(settings)
            // Last wins, so a test can point the directory somewhere of its own.
            .GroupBy(setting => setting.Key)
            .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)));

        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services;
    }

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }
}
