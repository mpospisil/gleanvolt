using System.Net;
using Gleanvolt.Api;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Setup;
using Gleanvolt.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// #301: a configuration the controller cannot run on starts the web UI in setup mode instead of
/// stopping the process -- and never starts the controller on part of it.
/// </summary>
public class SetupModeRegistrationTests
{
    private static readonly SetupHost Prague =
        new(TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"), IsWindows: false);

    private static IServiceCollection Register(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(settings
            .GroupBy(setting => setting.Key)
            .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)));
        builder.Services.AddGleanvolt(builder.Configuration, Prague);

        return builder.Services;
    }

    private static List<Type?> Workers(IServiceCollection services) =>
        [.. services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)];

    [Fact]
    public async Task Empty_device_addresses_start_setup_mode_with_no_controller()
    {
        var services = Register();

        Assert.Empty(Workers(services));

        await using var provider = services.BuildServiceProvider();
        var setup = provider.GetRequiredService<ISetupState>();

        Assert.True(setup.SetupMode);
        Assert.Contains(setup.AtStartup.MissingRequired, problem => problem.Area == Core.Models.SetupArea.Inverter);

        // Both clients refuse: nothing reached by accident can open a socket to an unconfirmed address.
        var inverter = provider.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.Inverter);
        var charger = provider.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.EvCharger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => inverter.ConnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => charger.WriteSingleRegisterAsync(0x60D, 0));
    }

    [Fact]
    public void A_usable_configuration_starts_the_controller()
    {
        var services = Register(UsableSite.Settings);

        var workers = Workers(services);

        Assert.Contains(typeof(PollingService), workers);
        Assert.Contains(typeof(Sessions.SessionRecordingWorker), workers);
        Assert.Contains(typeof(Monitoring.EnergyMonitorWorker), workers);
    }

    [Theory]
    [InlineData("Pv:Chargers:0:Host", "")]            // half the devices
    [InlineData("Pv:Inverter:Port", "70000")]         // a device that is configured wrong
    [InlineData("Pv:Longitude", null)]                // half a location
    [InlineData("Vehicle:BatteryCapacityKWh", "77")]  // a key that has moved
    [InlineData("Web:PasswordHash", "")]              // no password yet
    public void Any_required_problem_keeps_every_worker_out(string key, string? value)
    {
        var services = Register([.. UsableSite.Settings, (key, value)]);

        Assert.Empty(Workers(services));
    }

    [Fact]
    public void Headless_an_unusable_configuration_still_refuses_to_start()
    {
        var error = Assert.Throws<ConfigurationRefusedException>(() => Register(("Web:Enabled", "false")));

        Assert.Contains("Web:Enabled=false", error.Message);
        Assert.Contains("Pv:Inverter:Host", error.Message);
    }

    [Fact]
    public async Task The_api_answers_503_in_setup_mode()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection([new("Api:Enabled", "true"), new("Api:Keys:test", "a-key")]);
        builder.Services.AddGleanvolt(builder.Configuration, Prague);

        await using var app = builder.Build();

        // What UseGleanvolt mounts for the API, without the UI's static assets a test host has none of.
        app.UseGleanvoltSetupMode();
        app.MapGleanvoltApi(
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value, app.Logger);
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "a-key");
        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not configured", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await app.StopAsync();
    }

    [Fact]
    public async Task What_is_saved_since_startup_is_judged_afresh()
    {
        var root = Directory.CreateTempSubdirectory("gleanvolt-setup-state-").FullName;
        try
        {
            var path = Path.Combine(root, "pv-system.json");
            var builder = WebApplication.CreateSlimBuilder();
            builder.Configuration.AddInMemoryCollection(
                [new("Pv:OverridesPath", path), new("Controller:TimeZone", "Europe/Prague")]);
            builder.Configuration.AddPvSystemOverrides(root);
            builder.Services.AddGleanvolt(builder.Configuration, Prague);

            await using var provider = builder.Services.BuildServiceProvider();
            var setup = provider.GetRequiredService<ISetupState>();

            Assert.False(setup.Current().Usable);

            var saved = SetupDocument.From(new Dictionary<string, string>
            {
                ["Pv:Inverter:Host"] = "192.168.1.10",
                ["Pv:Chargers:0:Host"] = "192.168.1.11",
                ["Pv:Latitude"] = "50.0755",
                ["Pv:Longitude"] = "14.4378",
                ["Web:Password"] = "correct horse",
            }).Apply(new SetupTarget(
                builder.Configuration,
                path,
                provider.GetRequiredService<ISecretStore>(),
                provider.GetRequiredService<Web.Auth.WebPasswordService>()));

            Assert.True(saved.Applied, string.Join("; ", saved.Problems));

            // Still setup mode -- this process started without a controller -- but nothing is missing.
            Assert.True(setup.SetupMode);
            Assert.True(setup.Current().Usable, string.Join("; ", setup.Current().MissingRequired.Select(problem => problem.English)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
