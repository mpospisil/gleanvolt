using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Providers.Tests;

/// <summary>The echo kind's hub on a TestServer, with the real SignalR client against it.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private WebApplication? _app;

    public bool Enabled { get; set; } = true;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));

    public MemorySecrets Secrets { get; } = new();

    public ExternalProviderRegistry Registry => _app!.Services.GetRequiredService<ExternalProviderRegistry>();

    public ProviderSessions Sessions => Registry.For(EchoHub.Kind)!;

    public string Token { get; private set; } = string.Empty;

    public TestServer Server => _app!.GetTestServer();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ISecretStore>(Secrets);
        builder.Services.AddSingleton<TimeProvider>(Time);
        builder.Services.AddSingleton(new ProviderHostInfo("1.2.3-test"));
        builder.Services.AddExternalProvider<EchoHub>(new ProviderKind(EchoHub.Kind, "Echo", 1, 2, EchoHub.Path, _ => Enabled));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        _app.MapExternalProviderPages();
        _app.MapProviderPairing();
        await _app.StartAsync();

        Token = await Registry.CreateTokenAsync();
    }

    public void Configure(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options, string? token = null, IPAddress? from = null)
    {
        options.HttpMessageHandlerFactory = _ => from is null
            ? Server.CreateHandler()
            : Server.CreateHandler(context => context.Connection.RemoteIpAddress = from);
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(token ?? Token);
    }

    /// <summary>A raw connection, for checking the protocol itself; <paramref name="from"/> is the address it comes from.</summary>
    public async Task<HubConnection> ConnectAsync(string? token = null, IPAddress? from = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, EchoHub.Path), options => Configure(options, token, from))
            .Build();
        await connection.StartAsync();
        return connection;
    }

    /// <summary>Posts JSON to <paramref name="path"/> as a client at <paramref name="from"/> (127.0.0.1 when not said).</summary>
    public Task<HttpResponseMessage> PostAsync(string path, object body, IPAddress? from = null) =>
        PostRawAsync(path, System.Text.Json.JsonSerializer.Serialize(body, System.Text.Json.JsonSerializerOptions.Web), from);

    /// <summary>Posts <paramref name="json"/> as it is, for a body that is not what the contract sends.</summary>
    public async Task<HttpResponseMessage> PostRawAsync(string path, string json, IPAddress? from = null)
    {
        using var client = new HttpClient(Server.CreateHandler(context => context.Connection.RemoteIpAddress = from ?? IPAddress.Loopback))
        {
            BaseAddress = Server.BaseAddress,
        };
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        return await client.PostAsync(path, content);
    }

    public static ProviderHello Hello(string id = "echo-one", int major = 1) => new(id, "Echo " + id, "0.1", major, 0);

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}

internal sealed class MemorySecrets : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    /// <summary>How long a write takes, so two at once overlap.</summary>
    public TimeSpan WriteDelay { get; set; }

    public string? Read(string name)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    public bool Write(string name, string value)
    {
        if (WriteDelay > TimeSpan.Zero)
        {
            Thread.Sleep(WriteDelay);
        }

        lock (_values)
        {
            _values[name] = value;
            return true;
        }
    }

    public bool Delete(string name)
    {
        lock (_values)
        {
            return _values.Remove(name) || true;
        }
    }

    public string Describe() => "memory";
}

internal static class Eventually
{
    public static async Task True(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }
}
