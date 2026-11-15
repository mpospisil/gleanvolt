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
        await _app.StartAsync();

        Token = await Registry.CreateTokenAsync();
    }

    public void Configure(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options, string? token = null)
    {
        options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(token ?? Token);
    }

    /// <summary>A raw connection, for checking the protocol itself.</summary>
    public async Task<HubConnection> ConnectAsync(string? token = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, EchoHub.Path), options => Configure(options, token))
            .Build();
        await connection.StartAsync();
        return connection;
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

    public string? Read(string name)
    {
        lock (_values)
        {
            return _values.GetValueOrDefault(name);
        }
    }

    public bool Write(string name, string value)
    {
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
