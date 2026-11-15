using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// The provider framework (#386) through the real SignalR client: the handshake, one session per kind,
/// host requests that are never awaited, and the SDK's hello after every reconnect.
/// </summary>
public sealed class ProviderFrameworkTests : IAsyncLifetime
{
    private readonly TestHost _host = new();

    public Task InitializeAsync() => _host.StartAsync();

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Fact]
    public async Task AnEnabledKindWithTheRightTokenIsWelcomed()
    {
        await using var connection = await _host.ConnectAsync();

        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        Assert.True(welcome.Accepted);
        Assert.Equal("1.2.3-test", welcome.HostVersion);
        Assert.Equal(1, welcome.ContractMajor);
        Assert.Equal(2, welcome.ContractMinor);

        var status = _host.Registry.Find(EchoHub.Kind)!;
        Assert.Equal(ExternalProviderState.Connected, status.State);
        Assert.Equal("echo-one", status.ProviderId);
        Assert.Equal(_host.Time.GetUtcNow(), status.Since);
    }

    [Fact]
    public async Task ADisabledKindIsRefusedWithItsReason()
    {
        _host.Enabled = false;
        await using var connection = await _host.ConnectAsync();

        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        Assert.False(welcome.Accepted);
        Assert.Equal(RefusalCodes.Disabled, welcome.ReasonCode);
        Assert.Equal(ExternalProviderState.Off, _host.Registry.Find(EchoHub.Kind)!.State);
    }

    [Fact]
    public async Task AWrongTokenGets401()
    {
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => _host.ConnectAsync("gvp_wrong"));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, failure.StatusCode);
    }

    [Fact]
    public async Task NoTokenGets401()
    {
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => _host.ConnectAsync(string.Empty));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, failure.StatusCode);
    }

    [Fact]
    public async Task ARegeneratedTokenRetiresTheOldOne()
    {
        var old = _host.Token;
        await _host.Registry.CreateTokenAsync(EchoHub.Kind);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => _host.ConnectAsync(old));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, failure.StatusCode);
    }

    [Fact]
    public async Task AnotherContractMajorIsRefusedWithItsReason()
    {
        await using var connection = await _host.ConnectAsync();

        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello(major: 2));

        Assert.False(welcome.Accepted);
        Assert.Equal(RefusalCodes.ContractMajor, welcome.ReasonCode);
        Assert.Contains("2.0", welcome.Reason);
    }

    [Fact]
    public async Task AnInvalidProviderIdIsRefused()
    {
        await using var connection = await _host.ConnectAsync();

        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello(id: "Not Valid"));

        Assert.Equal(RefusalCodes.Invalid, welcome.ReasonCode);
    }

    [Fact]
    public async Task AMessageLargerThanSignalRsDefaultIsAccepted()
    {
        // #388: a week of half-hour forecast periods is past SignalR's 32 KB default, and a message over
        // the limit closes the connection instead of failing the call.
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        var result = await connection.InvokeAsync<PublishResult>("Publish", new string('x', 200 * 1024), (RequestId?)null);

        Assert.True(result.Accepted);
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task NothingIsAcceptedBeforeTheHello()
    {
        await using var connection = await _host.ConnectAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<PublishResult>("Publish", "text", (RequestId?)null));
    }

    [Fact]
    public async Task TheSameProviderReconnectingReplacesItsSession()
    {
        await using var first = await _host.ConnectAsync();
        Assert.True((await first.InvokeAsync<Welcome>("Hello", TestHost.Hello())).Accepted);
        var goodbye = new TaskCompletionSource<string>();
        first.On<string, string>("Goodbye", (code, _) => goodbye.TrySetResult(code));

        await using var second = await _host.ConnectAsync();
        Assert.True((await second.InvokeAsync<Welcome>("Hello", TestHost.Hello())).Accepted);

        Assert.Equal("replaced", await goodbye.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(_host.Sessions.IsCurrent(second.ConnectionId!));
    }

    [Fact]
    public async Task ADisconnectFromAReplacedConnectionChangesNothing()
    {
        var first = await _host.ConnectAsync();
        await first.InvokeAsync<Welcome>("Hello", TestHost.Hello());
        await using var second = await _host.ConnectAsync();
        await second.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        await first.DisposeAsync();
        await Task.Delay(200);

        Assert.Equal(ExternalProviderState.Connected, _host.Registry.Find(EchoHub.Kind)!.State);
        Assert.True(_host.Sessions.IsCurrent(second.ConnectionId!));
    }

    [Fact]
    public async Task ASecondProviderIsRefusedWhileTheFirstIsConnected()
    {
        await using var first = await _host.ConnectAsync();
        await first.InvokeAsync<Welcome>("Hello", TestHost.Hello("echo-one"));
        await using var second = await _host.ConnectAsync();

        var welcome = await second.InvokeAsync<Welcome>("Hello", TestHost.Hello("echo-two"));

        Assert.False(welcome.Accepted);
        Assert.Equal(RefusalCodes.AnotherProvider, welcome.ReasonCode);
        Assert.Equal("echo-one", _host.Registry.Find(EchoHub.Kind)!.ProviderId);
    }

    [Fact]
    public async Task ADisconnectLeavesTheProviderDisconnected()
    {
        var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        await connection.DisposeAsync();

        await Eventually.True(() => _host.Registry.Find(EchoHub.Kind)!.State == ExternalProviderState.Disconnected, "the session should end");
    }

    [Fact]
    public async Task TurningTheKindOffClosesTheSession()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());
        var goodbye = new TaskCompletionSource<string>();
        connection.On<string, string>("Goodbye", (code, _) => goodbye.TrySetResult(code));

        _host.Enabled = false;
        await _host.Registry.RevalidateAsync(EchoHub.Kind);

        Assert.Equal(RefusalCodes.Disabled, await goodbye.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ExternalProviderState.Off, _host.Registry.Find(EchoHub.Kind)!.State);
    }

    [Fact]
    public async Task ARequestWithNoReplyBecomesUnanswered()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        var request = _host.Sessions.BeginRequest(TimeSpan.FromMinutes(2));
        Assert.NotNull(request);

        _host.Time.Advance(TimeSpan.FromMinutes(3));

        await Eventually.True(() => _host.Registry.Find(EchoHub.Kind)!.Unanswered, "the request should time out");
    }

    [Fact]
    public async Task AnAnswerQuotingTheRequestClearsIt()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());
        var request = _host.Sessions.BeginRequest(TimeSpan.FromMinutes(2))!.Value;

        var result = await connection.InvokeAsync<PublishResult>("Publish", "pong", request.Id);
        _host.Time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(100);

        Assert.True(result.Accepted);
        var status = _host.Registry.Find(EchoHub.Kind)!;
        Assert.False(status.Unanswered);
        Assert.Equal(_host.Time.GetUtcNow() - TimeSpan.FromMinutes(3), status.LastDataAt);
    }

    [Fact]
    public async Task RefusedDataIsShownAsTheLastProblem()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        var result = await connection.InvokeAsync<PublishResult>("Publish", "bad", (RequestId?)null);

        Assert.False(result.Accepted);
        Assert.Equal(["Bad text."], _host.Registry.Find(EchoHub.Kind)!.LastProblem!);
    }

    [Fact]
    public async Task NoRequestWithoutASession()
    {
        Assert.Null(_host.Sessions.BeginRequest(TimeSpan.FromMinutes(2)));
        Assert.Equal(ExternalProviderState.Waiting, _host.Registry.Find(EchoHub.Kind)!.State);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheStatusReportIsKept()
    {
        await using var connection = await _host.ConnectAsync();
        await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());

        await connection.InvokeAsync("ReportStatus", new ProviderStatus(false, "Source down."));

        var status = _host.Registry.Find(EchoHub.Kind)!;
        Assert.False(status.Healthy);
        Assert.Equal("Source down.", status.StatusMessage);
    }

    [Fact]
    public async Task TheSdkSaysHelloAgainAfterTheConnectionDrops()
    {
        using var sdk = new EchoConnection(Options());
        var welcomes = 0;
        sdk.Welcomed += _ => Interlocked.Increment(ref welcomes);

        await sdk.StartAsync(CancellationToken.None);
        await Eventually.True(() => welcomes == 1 && sdk.IsAttached, "the SDK should attach");

        _host.Sessions.DropConnection();

        await Eventually.True(() => welcomes == 2 && sdk.IsAttached, "the SDK should say hello again");
        Assert.Equal(ExternalProviderState.Connected, _host.Registry.Find(EchoHub.Kind)!.State);
        await sdk.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheSdkKeepsTryingUntilTheKindIsTurnedOn()
    {
        _host.Enabled = false;
        using var sdk = new EchoConnection(Options());
        await sdk.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.False(sdk.IsAttached);

        _host.Enabled = true;

        await Eventually.True(() => sdk.IsAttached, "the SDK should attach once the kind is on");
        await sdk.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheSdkAnswersAHostRequest()
    {
        using var sdk = new EchoConnection(Options());
        await sdk.StartAsync(CancellationToken.None);
        await Eventually.True(() => sdk.IsAttached, "the SDK should attach");

        var request = _host.Sessions.BeginRequest(TimeSpan.FromMinutes(2))!.Value;
        await _host.Server.Services.GetRequiredService<IHubContext<EchoHub, IEchoClient>>()
            .Clients.Client(request.ConnectionId).Ping(new RefreshRequest(request.Id, _host.Time.GetUtcNow()));

        await Eventually.True(() => _host.Registry.Find(EchoHub.Kind)!.LastDataAt is not null, "the SDK should answer");
        await sdk.StopAsync(CancellationToken.None);
    }

    private ProviderOptions Options() => new()
    {
        Endpoint = _host.Server.BaseAddress,
        Token = _host.Token,
        ProviderId = "echo-sdk",
        Name = "Echo SDK",
        Version = "1.0",
        InitialRetryDelay = TimeSpan.FromMilliseconds(50),
        MaxRetryDelay = TimeSpan.FromMilliseconds(200),
        RefusedRetryDelay = TimeSpan.FromMilliseconds(100),
        ConfigureHttp = options => _host.Configure(options),
    };
}
