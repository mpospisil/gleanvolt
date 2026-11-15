using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// One token for every kind (#406), and the move to it from a token per kind (#386): a plugin that
/// connected with its kind's token keeps connecting after the upgrade.
/// </summary>
public class ProviderTokensTests
{
    private readonly MemorySecrets _secrets = new();

    [Fact]
    public void AMadeTokenMatchesAndAnotherDoesNot()
    {
        var tokens = new ProviderTokens(_secrets);
        var token = tokens.Create()!;

        Assert.StartsWith("gvp_", token);
        Assert.True(tokens.Exists);
        Assert.True(tokens.Matches(token));
        Assert.False(tokens.Matches(token + "x"));
        Assert.False(tokens.Matches(null));
        Assert.Equal(token, _secrets.Read(SecretNames.ProviderToken));
    }

    [Fact]
    public void WithoutATokenNothingMatches()
    {
        var tokens = new ProviderTokens(_secrets, ["tariff", "solar-forecast"]);

        Assert.False(tokens.Exists);
        Assert.False(tokens.Matches("gvp_anything"));
        Assert.Null(tokens.AdoptedFrom);
    }

    [Fact]
    public void TheFirstKindsOwnTokenIsAdoptedAndEveryKindsOwnIsRemoved()
    {
        _secrets.Write(SecretNames.LegacyProviderToken("tariff"), "gvp_tariff");
        _secrets.Write(SecretNames.LegacyProviderToken("solar-forecast"), "gvp_forecast");

        var tokens = new ProviderTokens(_secrets, ["tariff", "solar-forecast"]);

        Assert.Equal("tariff", tokens.AdoptedFrom);
        Assert.True(tokens.Matches("gvp_tariff"));
        Assert.False(tokens.Matches("gvp_forecast"));
        Assert.Null(_secrets.Read(SecretNames.LegacyProviderToken("tariff")));
        Assert.Null(_secrets.Read(SecretNames.LegacyProviderToken("solar-forecast")));
    }

    [Fact]
    public void OnlyALaterKindsTokenIsAdoptedToo()
    {
        _secrets.Write(SecretNames.LegacyProviderToken("solar-forecast"), "gvp_forecast");

        var tokens = new ProviderTokens(_secrets, ["tariff", "solar-forecast"]);

        Assert.Equal("solar-forecast", tokens.AdoptedFrom);
        Assert.True(tokens.Matches("gvp_forecast"));
    }

    [Fact]
    public void AnExistingSharedTokenWinsAndTheLeftoversAreRemoved()
    {
        _secrets.Write(SecretNames.ProviderToken, "gvp_shared");
        _secrets.Write(SecretNames.LegacyProviderToken("tariff"), "gvp_tariff");

        var tokens = new ProviderTokens(_secrets, ["tariff"]);

        Assert.Null(tokens.AdoptedFrom);
        Assert.True(tokens.Matches("gvp_shared"));
        Assert.False(tokens.Matches("gvp_tariff"));
        Assert.Null(_secrets.Read(SecretNames.LegacyProviderToken("tariff")));
    }
}
