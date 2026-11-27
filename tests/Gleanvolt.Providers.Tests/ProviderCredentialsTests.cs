using Gleanvolt.Providers.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Tests;

public sealed class ProviderCredentialsTests : IDisposable
{
    private static readonly Uri Paired = new("http://gleanvolt.local:8090/");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gleanvolt-credentials-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ProviderCredentials Credentials(string token = "", IProviderSecretStore? store = null, Uri? endpoint = null) =>
        new(Options.Create(new ProviderOptions { Token = token, Endpoint = endpoint }), store ?? new ProviderSecretStore(_directory));

    [Fact]
    public void AConfiguredTokenWins()
    {
        new ProviderSecretStore(_directory).Write("gleanvolt-token", "t2");

        var credentials = Credentials("t1");

        Assert.Equal("t1", credentials.Token);
        Assert.Equal(CredentialSource.Configuration, credentials.Source);
    }

    [Fact]
    public void AnEmptyConfiguredTokenCountsAsNone()
    {
        var credentials = Credentials("  ");

        Assert.Null(credentials.Token);
        Assert.Equal(CredentialSource.None, credentials.Source);
    }

    [Fact]
    public void APairedTokenSurvivesARestart()
    {
        Assert.True(Credentials().Save(Paired, "t"));

        var restarted = Credentials();

        Assert.Equal(CredentialSource.Paired, restarted.Source);
        Assert.Equal("t", restarted.Token);
        Assert.Equal(Paired, restarted.Endpoint);
    }

    [Fact]
    public void AConfiguredEndpointWinsOverThePairedOne()
    {
        // Paired over mDNS at an address Gleanvolt has since lost: the owner points the plugin at the new one.
        var moved = new Uri("http://192.0.2.9:8090/");
        Assert.True(Credentials().Save(Paired, "t"));

        var restarted = Credentials(endpoint: moved);

        Assert.Equal(CredentialSource.Paired, restarted.Source);
        Assert.Equal("t", restarted.Token);
        Assert.Equal(moved, restarted.Endpoint);
        Assert.False(restarted.EndpointIsPaired);
    }

    [Fact]
    public void ThePairedEndpointWinsOverTheDefault()
    {
        var credentials = Credentials();
        Assert.Equal(new Uri("http://localhost:8090"), credentials.Endpoint);
        Assert.False(credentials.EndpointIsPaired);

        credentials.Save(Paired, "t");

        Assert.Equal(Paired, credentials.Endpoint);
        Assert.True(credentials.EndpointIsPaired);
    }

    [Fact]
    public async Task APairedConnectionDialsTheConfiguredEndpoint()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        // Saved by an earlier pairing, at an address nothing answers on (TEST-NET-1).
        new ProviderCredentials(Options.Create(new ProviderOptions()), new ProviderSecretStore(_directory))
            .Save(new Uri("http://192.0.2.5:8090"), host.Token);

        var (connection, credentials) = Connect(host, token: "");
        await connection.StartAsync(CancellationToken.None);

        Assert.Equal(CredentialSource.Paired, credentials.Source);
        await Eventually.True(() => connection.IsAttached, "the paired token is used at the configured endpoint");
        await connection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void APairedTokenThatCannotBeWrittenIsStillUsed()
    {
        var credentials = Credentials(store: new RefusingStore());
        var changed = 0;
        credentials.Changed += () => changed++;

        Assert.False(credentials.Save(Paired, "t"));

        Assert.Equal("t", credentials.Token);
        Assert.Equal(CredentialSource.Paired, credentials.Source);
        Assert.Equal(1, changed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not an address")]
    public void ASavedTokenWithoutAUsableEndpointLoadsAsNone(string? endpoint)
    {
        var store = new ProviderSecretStore(_directory);
        store.Write("gleanvolt-token", "t");
        if (endpoint is not null)
        {
            store.Write("gleanvolt-endpoint", endpoint);
        }

        var credentials = Credentials();

        Assert.Equal(CredentialSource.None, credentials.Source);
        Assert.Null(credentials.Token);
        Assert.False(File.Exists(Path.Combine(_directory, "gleanvolt-token")));
    }

    [Fact]
    public void AFailingChangedHandlerStopsNeitherSaveNorForget()
    {
        var credentials = Credentials();
        var heard = 0;
        credentials.Changed += () => throw new InvalidOperationException("boom");
        credentials.Changed += () => heard++;

        Assert.True(credentials.Save(Paired, "t"));
        credentials.ForgetPaired();

        Assert.Equal(2, heard);
        Assert.Equal(CredentialSource.None, credentials.Source);
    }

    [Fact]
    public async Task AFailingChangedHandlerDoesNotStopTheConnectionLoop()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var (connection, credentials) = Connect(host, token: "");
        credentials.Changed += () => throw new InvalidOperationException("boom");
        await connection.StartAsync(CancellationToken.None);

        credentials.Save(host.Server.BaseAddress, "wrong");
        await Eventually.True(() => credentials.Source == CredentialSource.None, "the refusal was handled despite the handler");

        credentials.Save(host.Server.BaseAddress, host.Token);
        await Eventually.True(() => connection.IsAttached, "the loop is still running");
        await connection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void ForgettingClearsTokenAndEndpoint()
    {
        var credentials = Credentials();
        credentials.Save(Paired, "t");

        credentials.ForgetPaired();

        Assert.Equal(CredentialSource.None, credentials.Source);
        Assert.Equal(new Uri("http://localhost:8090"), credentials.Endpoint);
        Assert.False(File.Exists(Path.Combine(_directory, "gleanvolt-token")));
        Assert.False(File.Exists(Path.Combine(_directory, "gleanvolt-endpoint")));
    }

    [Fact]
    public void ForgettingLeavesAConfiguredTokenAlone()
    {
        var credentials = Credentials("t1");

        credentials.ForgetPaired();

        Assert.Equal("t1", credentials.Token);
    }

    [Fact]
    public async Task AConnectionWithoutATokenWaitsAndThenAttaches()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var (connection, credentials) = Connect(host, token: "");

        await connection.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.False(connection.IsAttached);

        credentials.Save(host.Server.BaseAddress, host.Token);

        await Eventually.True(() => connection.IsAttached, "a token that arrives is used without a restart");
        await connection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARefusedPairedTokenIsForgotten()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var (connection, credentials) = Connect(host, token: "");
        await connection.StartAsync(CancellationToken.None);

        credentials.Save(host.Server.BaseAddress, "wrong");

        await Eventually.True(() => credentials.Source == CredentialSource.None, "a refused paired token is dropped");
        Assert.False(connection.IsAttached);
        await connection.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ARefusedConfiguredTokenIsKept()
    {
        await using var host = new TestHost();
        await host.StartAsync();
        var (connection, credentials) = Connect(host, token: "wrong");
        await connection.StartAsync(CancellationToken.None);

        await Task.Delay(500);

        Assert.Equal(CredentialSource.Configuration, credentials.Source);
        Assert.False(connection.IsAttached);
        await connection.StopAsync(CancellationToken.None);
    }

    private (EchoConnection Connection, ProviderCredentials Credentials) Connect(TestHost host, string token)
    {
        var options = new ProviderOptions
        {
            Endpoint = host.Server.BaseAddress,
            Token = token,
            ProviderId = "echo-credentials",
            Name = "Echo credentials",
            Version = "1.0",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
            ConfigureHttp = http =>
            {
                // Only the test server's own address reaches it, so a test can tell which endpoint was dialled.
                http.HttpMessageHandlerFactory = _ => new OnlyAt(host.Server.BaseAddress, host.Server.CreateHandler());
                http.Transports = HttpTransportType.LongPolling;
            },
        };
        var credentials = new ProviderCredentials(Options.Create(options), new ProviderSecretStore(_directory));
        return (new EchoConnection(options, credentials: credentials), credentials);
    }

    private sealed class OnlyAt(Uri address, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri?.Authority == address.Authority
                ? base.SendAsync(request, cancellationToken)
                : throw new HttpRequestException($"Nothing listens at {request.RequestUri?.Authority}.");
    }

    private sealed class RefusingStore : IProviderSecretStore
    {
        public string Directory => "nowhere";

        public bool IsEncrypted => false;

        public string? Read(string name) => null;

        public bool Write(string name, string value) => false;

        public bool Delete(string name) => true;
    }
}
