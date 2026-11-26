using System.Net;
using System.Net.Http.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR.Client;

namespace Gleanvolt.Providers.Tests;

/// <summary>The pairing endpoints as a plugin without a token reaches them.</summary>
public sealed class PairingEndpointTests : IAsyncDisposable
{
    private static readonly IPAddress Local = IPAddress.Loopback;
    private readonly TestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static PairingKnock Knock(string secret = "secret-one", string hubPath = EchoHub.Path, string name = "Echo One", string providerId = "echo-one") =>
        new(hubPath, providerId, name, "0.1", null, PairingSecret.Hash(secret));

    private async Task<string> KnockAsync(PairingKnock? knock = null, IPAddress? from = null)
    {
        var response = await _host.PostAsync(PairingPaths.Knock, knock ?? Knock(), from);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingTicket>())!.RequestId;
    }

    private async Task<PairingAnswer> CollectAsync(string id, string secret = "secret-one")
    {
        var response = await _host.PostAsync(PairingPaths.Collect(id), new PairingCollect(secret));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PairingAnswer>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase) },
        }))!;
    }

    [Fact]
    public async Task AKnockIsListedForTheOwner()
    {
        await _host.StartAsync();

        var id = await KnockAsync(from: Local);

        var view = Assert.Single(_host.Registry.Pairing);
        Assert.Equal(id, view.Id);
        Assert.Equal(EchoHub.Kind, view.Kind);
        Assert.Equal("echo-one", view.ProviderId);
        Assert.Equal("Echo One", view.ProviderName);
        Assert.Equal(PairingSecret.Code(PairingSecret.Hash("secret-one")), view.Code);
        Assert.True(view.OnThisMachine);
        Assert.Equal(PairingBlock.None, view.Block);
    }

    [Fact]
    public async Task AnIPv4MappedAddressIsTheSameSourceAsTheIPv4One()
    {
        await _host.StartAsync();

        await KnockAsync(from: IPAddress.Parse("192.168.1.20"));
        await KnockAsync(from: IPAddress.Parse("192.168.1.20").MapToIPv6());

        var view = Assert.Single(_host.Registry.Pairing);
        Assert.Equal(PairingBlock.None, view.Block);
        Assert.Equal("192.168.1.20", view.Source);
    }

    [Fact]
    public async Task AnAcceptedPluginCollectsATokenThatOpensTheHub()
    {
        await _host.StartAsync();
        var id = await KnockAsync();
        Assert.Equal(PairingState.Waiting, (await CollectAsync(id)).State);

        Assert.True(await _host.Registry.AcceptPairingAsync(id));
        var answer = await CollectAsync(id);

        Assert.Equal(PairingState.Accepted, answer.State);
        Assert.Equal(_host.Token, answer.Token);
        await using var connection = await _host.ConnectAsync(answer.Token);
        var welcome = await connection.InvokeAsync<Welcome>("Hello", TestHost.Hello());
        Assert.NotNull(welcome);
    }

    [Fact]
    public async Task ARejectedPluginIsToldSo()
    {
        await _host.StartAsync();
        var id = await KnockAsync();

        _host.Registry.RejectPairing(id);

        Assert.Equal(PairingState.Rejected, (await CollectAsync(id)).State);
    }

    [Fact]
    public async Task AcceptCreatesTheTokenWhenThereIsNone()
    {
        await _host.StartAsync();
        _host.Secrets.Delete(SecretNames.ProviderToken);
        Assert.False(_host.Registry.Tokens.Exists);
        var id = await KnockAsync();

        Assert.True(await _host.Registry.AcceptPairingAsync(id));

        Assert.True(_host.Registry.Tokens.Exists);
        Assert.Equal(_host.Registry.Tokens.Read(), (await CollectAsync(id)).Token);
    }

    [Fact]
    public async Task AKnockForAKindThatIsOffIsBlocked()
    {
        _host.Enabled = false;
        await _host.StartAsync();
        var id = await KnockAsync();

        Assert.Equal(PairingBlock.KindOff, Assert.Single(_host.Registry.Pairing).Block);
        Assert.False(await _host.Registry.AcceptPairingAsync(id));
    }

    [Fact]
    public async Task TwoSourcesClaimingOneIdAreBothBlocked()
    {
        await _host.StartAsync();
        var first = await KnockAsync(from: IPAddress.Parse("192.168.1.20"));
        await KnockAsync(Knock("secret-two"), IPAddress.Parse("192.168.1.21"));

        Assert.All(_host.Registry.Pairing, view => Assert.Equal(PairingBlock.Duplicate, view.Block));
        Assert.False(await _host.Registry.AcceptPairingAsync(first));
    }

    [Fact]
    public async Task AKnockForAnUnknownHubIsRefused()
    {
        await _host.StartAsync();

        var response = await _host.PostAsync(PairingPaths.Knock, Knock(hubPath: "/providers/nothing"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Registry.Pairing);
    }

    [Fact]
    public async Task AProviderIdWithATrailingNewlineIsRefused()
    {
        await _host.StartAsync();

        var response = await _host.PostAsync(PairingPaths.Knock, Knock(providerId: "echo-one\n"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownSourceAddressIsNotThisMachine()
    {
        await _host.StartAsync();
        using var client = _host.Server.CreateClient();

        var response = await client.PostAsJsonAsync(PairingPaths.Knock, Knock());

        response.EnsureSuccessStatusCode();
        Assert.False(Assert.Single(_host.Registry.Pairing).OnThisMachine);
    }

    [Fact]
    public async Task ALongNameIsRefused()
    {
        await _host.StartAsync();

        var response = await _host.PostAsync(PairingPaths.Knock, Knock(name: new string('x', 101)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("not base64 url, but 43 characters long......")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n")]
    public async Task AMalformedSecretHashIsRefused(string hash)
    {
        await _host.StartAsync();

        var response = await _host.PostAsync(PairingPaths.Knock, Knock() with { SecretHash = hash });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.Registry.Pairing);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"hubPath\":5}")]
    [InlineData("{\"hubPath\":\"/providers/test-echo\",\"providerId\":\"echo-one\",\"name\":\"n\",\"version\":\"1\",\"pageUrl\":\"ftp://x\",\"secretHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    public async Task ABodyThatIsNotAKnockIsRefusedNotFailed(string json)
    {
        await _host.StartAsync();

        var response = await _host.PostRawAsync(PairingPaths.Knock, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ACollectWithoutASecretIsRefusedNotFailed()
    {
        await _host.StartAsync();
        var id = await KnockAsync();

        var response = await _host.PostRawAsync(PairingPaths.Collect(id), "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnOversizedBodyIsRefused()
    {
        await _host.StartAsync();

        var response = await _host.PostAsync(PairingPaths.Knock, Knock(name: new string('x', 5000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TheEleventhKnockInAMinuteIsTooMany()
    {
        await _host.StartAsync();

        for (var i = 0; i < PairingRequests.KnocksPerMinutePerSource; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _host.PostAsync(PairingPaths.Knock, Knock())).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await _host.PostAsync(PairingPaths.Knock, Knock())).StatusCode);
    }
}
