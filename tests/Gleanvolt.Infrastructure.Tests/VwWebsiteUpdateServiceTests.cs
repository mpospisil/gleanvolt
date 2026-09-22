using System.Net;
using System.Net.Sockets;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Vehicles.VwWebsite;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// volkswagen.de's two ways of being asked (issues #170, #212): the clock only while a charge runs,
/// and an owner's explicit ask whether or not one does.
///
/// <para>A loopback socket stands in for volkswagen.de and only counts connections: whether the wire
/// was touched is the whole question, and what a login answers is covered elsewhere.</para>
/// </summary>
public sealed class VwWebsiteUpdateServiceTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _sessionPath =
        Path.Combine(Path.GetTempPath(), $"gleanvolt-vw-website-{Guid.NewGuid():N}", "session.json");
    private readonly VwWebsiteClient _client;
    private readonly VwWebsiteUpdateService _service;
    private int _connections;

    public VwWebsiteUpdateServiceTests()
    {
        _listener.Start();
        _ = AcceptAsync();

        var options = new VwWebsiteOptions
        {
            Enabled = true,
            Username = "owner@example.com",
            Password = "hunter2",
            Vin = "WVGZZZE2ZPE999999",
            PortalBaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}",
            Timeout = TimeSpan.FromSeconds(2),
        };

        _client = new VwWebsiteClient(options, new VwWebsiteSessionStore(_sessionPath));

        // A holder that has never seen a poll: nothing is charging.
        _service = new VwWebsiteUpdateService(options, _client, new ChargeControlStatusHolder(), "id4");
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                using var socket = await _listener.AcceptSocketAsync();
                Interlocked.Increment(ref _connections);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _client.Dispose();

        var directory = Path.GetDirectoryName(_sessionPath)!;

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task The_clock_sends_nothing_while_the_car_is_not_charging()
    {
        Assert.Null(await _service.FetchAsync(CancellationToken.None));
        Assert.Equal(0, Volatile.Read(ref _connections));
    }

    [Fact]
    public async Task An_owner_s_ask_reads_volkswagen_de_although_the_car_is_parked()
    {
        // #212: with the portal gone, this is how a parked ID.4 gets a reading newer than its last
        // charge. The loopback answers nothing useful, so no reading comes back -- but it was asked.
        await _service.AskAsync(CancellationToken.None);

        Assert.True(Volatile.Read(ref _connections) > 0);
    }

    [Fact]
    public void It_is_named_in_the_owner_s_words_and_has_no_fix_to_offer_while_healthy()
    {
        Assert.Equal("volkswagen.de", _service.DisplayName);
        Assert.Null(_service.OwnerAction);
        Assert.True(_service.DeliversOnlyWhileCharging);
    }
}
