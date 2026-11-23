using Gleanvolt.Providers.Client;
using Microsoft.Extensions.Configuration;

using System.Net;
using Gleanvolt.Chargers;
using Gleanvolt.Providers.Testing;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// Gleanvolt.Chargers.Hosting (#443), what every charger plugin shares: the address, the settings, the search's
/// networks, the recording; and the testing package's fake Modbus charger.
/// </summary>
public sealed class ChargersHostingTests
{
    [Theory]
    [InlineData("192.168.1.6", 502, "192.168.1.6:502")]
    [InlineData(" charger.local:8080/ ", 80, "charger.local:8080")]
    [InlineData("http://192.168.1.30", 80, "192.168.1.30:80")]
    [InlineData("192.168.1.6:80", 502, "192.168.1.6:80")]
    [InlineData("http://192.168.1.6/rpc", 80, null)]
    [InlineData("ftp://192.168.1.6", 80, null)]
    [InlineData("http://admin:pw@192.168.1.6", 80, null)]
    [InlineData("", 80, null)]
    public void An_address_is_a_host_with_an_optional_port(string text, int defaultPort, string? expected)
    {
        Assert.Equal(expected, DeviceAddress.Parse(text, defaultPort)?.ToString());
    }

    [Fact]
    public void A_saved_setting_wins_over_the_configuration_and_survives_a_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gleanvolt-chargers-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Solax:Host"] = "192.168.1.6" })
                .Build();
            var plugin = new PluginInfo("solax-charger", "SolaX", "Solax", 8098, 502);
            var settings = new PluginSettings(new ProviderSecretStore(directory), configuration, plugin);

            Assert.Equal(SettingSource.Configuration, settings.Address.Source);
            settings.Address.Save("192.168.1.7");

            var restarted = new PluginSettings(new ProviderSecretStore(directory), configuration, plugin);
            Assert.Equal("192.168.1.7", restarted.Address.Current);
            Assert.Equal(SettingSource.Page, restarted.Address.Source);

            restarted.Address.Clear();
            Assert.Equal("192.168.1.6", restarted.Address.Current);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("192.168.1.0/24", 254)]
    [InlineData("10.0.4.0/22", 1022)]
    [InlineData("10.0.0.0/21", -1)]
    [InlineData("8.8.8.0/24", -1)]
    [InlineData("nonsense", -1)]
    public void The_search_takes_a_private_network_no_larger_than_a_22(string network, int hosts)
    {
        Assert.Equal(hosts, LanScan.Hosts(network)?.Count ?? -1);
    }

    [Fact]
    public async Task The_recording_keeps_the_path_and_never_the_authorization_header()
    {
        var recorder = new DeviceRecorder(TimeProvider.System);
        recorder.Start();
        using var http = new HttpClient(new RecordingHandler(recorder) { InnerHandler = new Answers() });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://192.168.1.50/rpc/Enum.GetStatus?owner=%22service:0%22&role=%22work_state%22");
        request.Headers.Authorization = new("Digest", "secret-response");

        await http.SendAsync(request);

        var json = recorder.ToJson("shelly-topac", "test", null);
        Assert.Contains("GET /rpc/Enum.GetStatus", json);
        Assert.Contains("charger_free", json);
        Assert.DoesNotContain("secret-response", json);
    }

    [Fact]
    public async Task Nothing_is_recorded_until_a_recording_starts()
    {
        var recorder = new DeviceRecorder(TimeProvider.System);
        using var http = new HttpClient(new RecordingHandler(recorder) { InnerHandler = new Answers() });

        await http.GetAsync("http://192.168.1.50/rpc/Enum.GetStatus?owner=%22service:0%22&role=%22work_state%22");

        Assert.Equal(0, recorder.Count);
    }

    [Fact]
    public async Task The_fake_Modbus_charger_answers_its_map_refuses_the_rest_and_keeps_every_write()
    {
        await using var charger = new FakeModbusServer();
        charger.Input[0x001D] = 2;
        var recorder = new DeviceRecorder(TimeProvider.System);
        var settings = new PluginSettings(
            new Gleanvolt.Providers.Client.ProviderSecretStore(Path.Combine(Path.GetTempPath(), "gleanvolt-hosting-" + Guid.NewGuid().ToString("N"))),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new PluginInfo("test", "Test", "Test", 8110, 502));
        await using var device = new ModbusDevice(
            settings.Address, settings.Get("unitId"), 502, 1, TimeSpan.Zero, recorder,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ModbusDevice>.Instance);
        settings.Address.Save(charger.Address.ToString());

        var runMode = await device.UseAsync(client => client.ReadInputRegistersAsync(0x001D, 1), CancellationToken.None);
        await device.UseAsync(async client =>
        {
            await client.WriteSingleRegisterAsync(0x0628, 1000);
            return true;
        }, CancellationToken.None);

        Assert.Equal([(ushort)2], runMode);
        Assert.Equal(["0x0628=1000"], charger.Writes);
        await Assert.ThrowsAnyAsync<Exception>(() => device.UseAsync(client => client.ReadInputRegistersAsync(0x0100, 1), CancellationToken.None));
    }

    // A device that answers every request with a work state.
    private sealed class Answers : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":"charger_free"}""") });
    }
}
