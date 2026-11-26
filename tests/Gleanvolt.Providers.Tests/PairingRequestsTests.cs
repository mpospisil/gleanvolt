using System.Net;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Server;
using Microsoft.Extensions.Time.Testing;

namespace Gleanvolt.Providers.Tests;

public sealed class PairingRequestsTests
{
    private static readonly ProviderKind Kind = new("echo", "Echo", 1, 2, "/providers/echo", _ => true);
    private static readonly IPAddress Lan = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Other = IPAddress.Parse("192.168.1.21");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero));
    private readonly PairingRequests _requests;

    public PairingRequestsTests() => _requests = new PairingRequests(_time);

    private static PairingKnock KnockOf(string secret, string id = "plugin-a") =>
        new("/providers/echo", id, "Plugin", "1.0", null, PairingSecret.Hash(secret));

    [Fact]
    public void KnockIsListedWithTheCodeOfItsSecretHash()
    {
        var knock = KnockOf("s1");

        var entry = _requests.Knock(Kind, knock, Lan);

        Assert.NotNull(entry);
        Assert.Equal(PairingSecret.Code(knock.SecretHash), entry.Code);
        Assert.Equal(entry.Id, Assert.Single(_requests.Waiting).Id);
    }

    [Fact]
    public void ALoopbackKnockIsOnThisMachine()
    {
        Assert.True(_requests.Knock(Kind, KnockOf("a", "a"), IPAddress.Loopback)!.OnThisMachine);
        Assert.True(_requests.Knock(Kind, KnockOf("b", "b"), IPAddress.IPv6Loopback)!.OnThisMachine);
        Assert.False(_requests.Knock(Kind, KnockOf("c", "c"), Lan)!.OnThisMachine);
    }

    [Fact]
    public void TheSameProviderFromTheSameAddressReplacesItsEntry()
    {
        _requests.Knock(Kind, KnockOf("old"), Lan);
        var second = KnockOf("new");
        _requests.Knock(Kind, second, Lan);

        var waiting = Assert.Single(_requests.Waiting);
        Assert.Equal(second.SecretHash, waiting.Knock.SecretHash);
    }

    [Fact]
    public void TheSameProviderFromTwoAddressesIsADuplicate()
    {
        var first = _requests.Knock(Kind, KnockOf("a"), Lan)!;
        var second = _requests.Knock(Kind, KnockOf("b"), Other)!;

        Assert.Equal(2, _requests.Waiting.Count);
        Assert.True(_requests.IsDuplicate(first));
        Assert.True(_requests.IsDuplicate(second));
        Assert.False(_requests.Accept(first.Id, "token"));
        Assert.False(_requests.Accept(second.Id, "token"));
    }

    [Fact]
    public void AWaitingCollectSaysWaiting()
    {
        var entry = _requests.Knock(Kind, KnockOf("s"), Lan)!;

        Assert.Equal(PairingState.Waiting, _requests.Collect(entry.Id, "s").State);
    }

    [Fact]
    public void TheWrongSecretIsUnknown()
    {
        var entry = _requests.Knock(Kind, KnockOf("s"), Lan)!;

        Assert.Equal(PairingState.Unknown, _requests.Collect(entry.Id, "other").State);
        Assert.Single(_requests.Waiting);
    }

    [Fact]
    public void AnAcceptedCollectGetsTheTokenOnceAndIsGone()
    {
        var entry = _requests.Knock(Kind, KnockOf("s"), Lan)!;

        Assert.True(_requests.Accept(entry.Id, "the-token"));
        Assert.Empty(_requests.Waiting);

        var answer = _requests.Collect(entry.Id, "s");
        Assert.Equal(PairingState.Accepted, answer.State);
        Assert.Equal("the-token", answer.Token);
        Assert.Equal(PairingState.Unknown, _requests.Collect(entry.Id, "s").State);
    }

    [Fact]
    public void ARejectIsToldOnceAndIsGone()
    {
        var entry = _requests.Knock(Kind, KnockOf("s"), Lan)!;

        _requests.Reject(entry.Id);

        Assert.Equal(PairingState.Rejected, _requests.Collect(entry.Id, "s").State);
        Assert.Equal(PairingState.Unknown, _requests.Collect(entry.Id, "s").State);
    }

    [Fact]
    public void AnEntryExpiresFifteenMinutesAfterItWasLastSeen()
    {
        var entry = _requests.Knock(Kind, KnockOf("s"), Lan)!;

        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(PairingState.Waiting, _requests.Collect(entry.Id, "s").State);

        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.Single(_requests.Waiting);

        _time.Advance(TimeSpan.FromMinutes(15));
        Assert.Empty(_requests.Waiting);
        Assert.Equal(PairingState.Unknown, _requests.Collect(entry.Id, "s").State);
    }

    [Fact]
    public void TheSeventeenthEntryDropsTheOldest()
    {
        var first = _requests.Knock(Kind, KnockOf("s0", "p0"), Lan)!;
        for (var i = 1; i < PairingRequests.MaxEntries; i++)
        {
            _requests.Knock(Kind, KnockOf($"s{i}", $"p{i}"), IPAddress.Parse($"10.0.0.{i}"));
        }

        Assert.Equal(PairingRequests.MaxEntries, _requests.Waiting.Count);
        _requests.Knock(Kind, KnockOf("last", "p-last"), Other);

        var waiting = _requests.Waiting;
        Assert.Equal(PairingRequests.MaxEntries, waiting.Count);
        Assert.DoesNotContain(waiting, e => e.Id == first.Id);
    }

    [Fact]
    public void TheEleventhKnockInAMinuteFromOneAddressIsRefused()
    {
        for (var i = 0; i < PairingRequests.KnocksPerMinutePerSource; i++)
        {
            Assert.NotNull(_requests.Knock(Kind, KnockOf($"s{i}", $"p{i}"), Lan));
        }

        Assert.Null(_requests.Knock(Kind, KnockOf("x", "px"), Lan));
        Assert.NotNull(_requests.Knock(Kind, KnockOf("y", "py"), Other));

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(_requests.Knock(Kind, KnockOf("z", "pz"), Lan));
    }
}
