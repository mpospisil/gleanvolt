using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The Solcast account in use (issue #286). What has to hold: the page's account wins over the
/// environment's and survives a restart; removing it hands back to the environment; an environment
/// value that is empty — which <c>docker-compose.yml</c> always sets — counts as no account; and every
/// change moves the version the forecast service files a refusal against.
/// </summary>
public class SolcastAccountStoreTests
{
    private static SolcastOptions Environment(string key = "env-key", string site = "env-site") =>
        new() { ApiKey = key, ResourceId = site };

    [Fact]
    public void TheEnvironmentsAccountIsUsedWhenThePageHoldsNone()
    {
        var store = new SolcastAccountStore(new MemorySecretStore(), Options.Create(Environment()));

        Assert.Equal(SolcastAccountSource.Environment, store.Source);
        Assert.Equal(new SolcastAccount("env-key", "env-site"), store.Current);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("key", "")]
    [InlineData("", "site")]
    [InlineData("  ", "site")]
    public void AnIncompleteEnvironmentIsNoAccount(string key, string site)
    {
        var store = new SolcastAccountStore(new MemorySecretStore(), Options.Create(Environment(key, site)));

        Assert.Equal(SolcastAccountSource.None, store.Source);
        Assert.Null(store.Current);
        Assert.False(store.EnvironmentConfigured);
    }

    [Fact]
    public void ThePagesAccountWinsAndOutlivesARestart()
    {
        var secrets = new MemorySecretStore();
        var store = new SolcastAccountStore(secrets, Options.Create(Environment()));

        Assert.True(store.Save(new SolcastAccount("page-key", "page-site")));

        var restarted = new SolcastAccountStore(secrets, Options.Create(Environment()));
        Assert.Equal(SolcastAccountSource.Page, restarted.Source);
        Assert.Equal(new SolcastAccount("page-key", "page-site"), restarted.Current);
    }

    [Fact]
    public void RemovingHandsBackToTheEnvironment()
    {
        var secrets = new MemorySecretStore();
        var store = new SolcastAccountStore(secrets, Options.Create(Environment()));
        store.Save(new SolcastAccount("page-key", "page-site"));

        store.Clear();

        Assert.Equal(SolcastAccountSource.Environment, store.Source);
        Assert.Null(secrets.Read(SecretNames.SolcastAccount));
    }

    [Fact]
    public void EveryChangeMovesTheVersion()
    {
        var store = new SolcastAccountStore(new MemorySecretStore(), Options.Create(Environment()));
        var start = store.Version;

        store.Save(new SolcastAccount("page-key", "page-site"));
        var saved = store.Version;
        store.Clear();

        Assert.True(saved > start);
        Assert.True(store.Version > saved);
    }

    [Fact]
    public void AnAccountTheStoreWouldNotKeepIsStillInUse()
    {
        var store = new SolcastAccountStore(new MemorySecretStore { Refuses = true }, Options.Create(new SolcastOptions()));

        Assert.False(store.Save(new SolcastAccount("page-key", "page-site")));
        Assert.Equal(SolcastAccountSource.Page, store.Source);
    }

    [Fact]
    public void AStoredValueThatCannotBeReadIsNoAccount()
    {
        var secrets = new MemorySecretStore();
        secrets.Write(SecretNames.SolcastAccount, "not json");

        var store = new SolcastAccountStore(secrets, Options.Create(Environment()));

        Assert.Equal(SolcastAccountSource.Environment, store.Source);
    }

    [Fact]
    public void TheKeyIsKeptOutOfToString()
    {
        Assert.DoesNotContain("secret-key", new SolcastAccount("secret-key", "site").ToString());
    }
}

/// <summary>A secret store in a dictionary. <see cref="Refuses"/> makes every write fail, as a full disk would.</summary>
internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];

    public bool Refuses { get; init; }

    public string? Read(string name) => _values.GetValueOrDefault(name);

    public bool Write(string name, string value)
    {
        if (Refuses)
        {
            return false;
        }

        _values[name] = value;
        return true;
    }

    public bool Delete(string name)
    {
        _values.Remove(name);
        return true;
    }

    public string Describe() => "memory";
}
